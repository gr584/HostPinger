using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class HostSummaryTests
    {
        /// <summary>
        /// Allowing no retries makes the first missed ping count as down, which is what the
        /// application did before the setting existed. Used by every test that is not about the
        /// retries themselves.
        /// </summary>
        private const int NoRetries = 0;

        /// <summary>
        /// Two retries, so a host is down on the third ping it misses in a row. Used by the tests
        /// that are about the retries, where <c>TwoRetries + 1</c> is the miss that makes it down.
        /// </summary>
        private const int TwoRetries = 2;

        private SqliteConnection _connection = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public void SetUp()
        {
            (_connection, _options) = TestDb.CreateInMemory();
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
        }

        [Test]
        public async Task LoadAsync_OrdersHostsByName()
        {
            await using var db = new HostPingerDbContext(_options);
            db.Hosts.AddRange(
                new MonitoredHost { Name = "charlie", Address = "c.example" },
                new MonitoredHost { Name = "alpha", Address = "a.example" },
                new MonitoredHost { Name = "bravo", Address = "b.example" });
            await db.SaveChangesAsync();

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.That(summaries.Select(s => s.Host.Name), Is.EqualTo(new[] { "alpha", "bravo", "charlie" }));
        }

        [Test]
        public async Task LoadAsync_ReturnsTheMostRecentAttemptPerHost()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var first = new MonitoredHost { Name = "first", Address = "first.example" };
            var second = new MonitoredHost { Name = "second", Address = "second.example" };
            db.Hosts.AddRange(first, second);
            await db.SaveChangesAsync();
            db.PingAttempts.AddRange(
                new PingAttempt { HostId = first.Id, TimestampUtc = start, RoundtripMs = 10 },
                // Inserted out of order so the result cannot come from insertion order alone.
                new PingAttempt { HostId = first.Id, TimestampUtc = start.AddMinutes(2), RoundtripMs = 30 },
                new PingAttempt { HostId = first.Id, TimestampUtc = start.AddMinutes(1), RoundtripMs = 20 },
                new PingAttempt { HostId = second.Id, TimestampUtc = start, RoundtripMs = 99 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].Last?.TimestampUtc, Is.EqualTo(start.AddMinutes(2)));
                Assert.That(summaries[0].Last?.RoundtripMs, Is.EqualTo(30));
                Assert.That(summaries[1].Last?.RoundtripMs, Is.EqualTo(99));
            });
        }

        [Test]
        public async Task LoadAsync_LeavesLastNullForAHostThatWasNeverPinged()
        {
            await using var db = new HostPingerDbContext(_options);
            db.Hosts.Add(new MonitoredHost { Name = "fresh", Address = "fresh.example" });
            await db.SaveChangesAsync();

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.That(summaries.Single().Last, Is.Null);
        }

        /// <summary>
        /// A host that answered nothing must still report an attempt — the page tells "Down" from
        /// "Waiting…" by whether <see cref="HostSummary.Last"/> exists, so folding an unanswered
        /// ping into null would mislabel a host that is down as one that has never been pinged.
        /// </summary>
        [Test]
        public async Task LoadAsync_ReportsAnUnansweredPingAsAnAttemptWithoutARoundtrip()
        {
            var timestamp = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = new MonitoredHost { Name = "down", Address = "down.example" };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = timestamp, RoundtripMs = null });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.Multiple(() =>
            {
                Assert.That(summaries.Single().Last, Is.Not.Null);
                Assert.That(summaries.Single().Last!.TimestampUtc, Is.EqualTo(timestamp));
                Assert.That(summaries.Single().Last!.RoundtripMs, Is.Null);
            });
        }

        [Test]
        public async Task LoadAsync_ReportsTheLastDowntimeBetweenTheAnsweredPingsAroundIt()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "flaky");
            // Up, an earlier outage, up, the last outage (unanswered at minutes 4 and 5), up again.
            await AddAttemptsAsync(db, host, start, [10, null, 20, 30, null, null, 40, 50]);

            var downtime = (await HostSummary.LoadAsync(db, NoRetries)).Single().LastDowntime;

            Assert.Multiple(() =>
            {
                Assert.That(downtime, Is.Not.Null);
                Assert.That(downtime!.StartedUtc, Is.EqualTo(start.AddMinutes(3)), "measured from the last answered ping");
                Assert.That(downtime.EndedUtc, Is.EqualTo(start.AddMinutes(6)));
                Assert.That(downtime.IsOngoing, Is.False);
                Assert.That(downtime.DurationAt(start.AddHours(1)), Is.EqualTo(TimeSpan.FromMinutes(3)));
            });
        }

        /// <summary>
        /// A downtime that has not ended yet has no answered ping to measure to, so it stays open
        /// and the page measures it against the current time instead.
        /// </summary>
        [Test]
        public async Task LoadAsync_LeavesTheDowntimeOpenWhileTheHostIsStillDown()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "down");
            await AddAttemptsAsync(db, host, start, [10, 20, null, null]);

            var downtime = (await HostSummary.LoadAsync(db, NoRetries)).Single().LastDowntime;

            Assert.Multiple(() =>
            {
                Assert.That(downtime!.StartedUtc, Is.EqualTo(start.AddMinutes(1)));
                Assert.That(downtime.EndedUtc, Is.Null);
                Assert.That(downtime.IsOngoing, Is.True);
                Assert.That(downtime.DurationAt(start.AddMinutes(5)), Is.EqualTo(TimeSpan.FromMinutes(4)));
            });
        }

        /// <summary>
        /// The bug this replaced: measuring from the first unanswered ping reads a stretch with no
        /// attempts in it as uptime, so an outage that began while the monitor was stopped was
        /// reported as starting when the monitor came back — six days late, for the host that
        /// turned this up. Nothing here says the host answered after 12:01, so nothing may claim it.
        /// </summary>
        [Test]
        public async Task LoadAsync_CountsDowntimeFromTheLastAnsweredPingAcrossAGapInMonitoring()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "unwatched");
            await AddAttemptsAsync(db, host, start, [10, 20]);
            // The monitor is stopped for six days, then comes back to an unanswered host.
            await AddAttemptsAsync(db, host, start.AddDays(6), [null, null]);

            var downtime = (await HostSummary.LoadAsync(db, NoRetries)).Single().LastDowntime;

            Assert.Multiple(() =>
            {
                Assert.That(downtime!.StartedUtc, Is.EqualTo(start.AddMinutes(1)));
                Assert.That(downtime.IsOngoing, Is.True);
                Assert.That(downtime.DurationAt(start.AddDays(6).AddMinutes(1)),
                    Is.EqualTo(TimeSpan.FromDays(6)));
            });
        }

        /// <summary>
        /// With no answered ping before the failures there is nothing to measure from, so the
        /// downtime falls back to the host's very first attempt.
        /// </summary>
        [Test]
        public async Task LoadAsync_StartsTheDowntimeAtTheFirstAttemptWhenTheHostNeverAnswered()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "silent");
            await AddAttemptsAsync(db, host, start, [null, null, null]);

            var downtime = (await HostSummary.LoadAsync(db, NoRetries)).Single().LastDowntime;

            Assert.Multiple(() =>
            {
                Assert.That(downtime!.StartedUtc, Is.EqualTo(start));
                Assert.That(downtime.EndedUtc, Is.Null);
            });
        }

        [Test]
        public async Task LoadAsync_LeavesTheDowntimeNullWhenEveryPingWasAnswered()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "healthy");
            await AddAttemptsAsync(db, host, start, [10, 20, 30]);

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.That(summaries.Single().LastDowntime, Is.Null);
        }

        [Test]
        public async Task LoadAsync_LeavesTheDowntimeNullForAHostThatWasNeverPinged()
        {
            await using var db = new HostPingerDbContext(_options);
            await AddHostAsync(db, "fresh");

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.That(summaries.Single().LastDowntime, Is.Null);
        }

        /// <summary>Each host's downtime must come from its own attempts, not another host's.</summary>
        [Test]
        public async Task LoadAsync_KeepsDowntimesPerHost()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var first = await AddHostAsync(db, "first");
            var second = await AddHostAsync(db, "second");
            await AddAttemptsAsync(db, first, start, [10, null, 20]);
            await AddAttemptsAsync(db, second, start, [10, 20, 30]);

            var summaries = await HostSummary.LoadAsync(db, NoRetries);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].LastDowntime!.StartedUtc, Is.EqualTo(start));
                Assert.That(summaries[0].LastDowntime!.EndedUtc, Is.EqualTo(start.AddMinutes(2)));
                Assert.That(summaries[1].LastDowntime, Is.Null);
            });
        }

        /// <summary>
        /// A run too short to make the host down is not an outage either, so nothing is reported.
        /// The column would otherwise contradict the status beside it — a host reading as retrying
        /// while the row next to it says it is in a downtime.
        /// </summary>
        [Test]
        public async Task LoadAsync_LeavesTheDowntimeNullForARunShorterThanTheThreshold()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "blippy");
            await AddAttemptsAsync(db, host, start, [10, null, null, 20]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.LastDowntime, Is.Null);
                Assert.That(summary.Status, Is.EqualTo(HostStatus.Up));
            });
        }

        /// <summary>
        /// The downtime opens at the threshold and is measured from the last answered ping, so the
        /// retries are inside it: the host was not answering for that time either. The threshold
        /// decides whether an outage is reported, not when it started.
        /// </summary>
        [Test]
        public async Task LoadAsync_OpensTheDowntimeAtTheThresholdAndCountsTheRetriesIntoIt()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "down");
            await AddAttemptsAsync(db, host, start, [10, null, null, null]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.Status, Is.EqualTo(HostStatus.Down));
                Assert.That(summary.LastDowntime!.StartedUtc, Is.EqualTo(start));
                Assert.That(summary.LastDowntime.IsOngoing, Is.True);
                Assert.That(summary.LastDowntime.DurationAt(start.AddMinutes(3)),
                    Is.EqualTo(TimeSpan.FromMinutes(3)));
            });
        }

        [Test]
        public async Task LoadAsync_ReportsADowntimeThatReachedTheThresholdAndThenEnded()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "recovered");
            await AddAttemptsAsync(db, host, start, [10, null, null, null, 20]);

            var downtime = (await HostSummary.LoadAsync(db, TwoRetries)).Single().LastDowntime;

            Assert.Multiple(() =>
            {
                Assert.That(downtime!.StartedUtc, Is.EqualTo(start));
                Assert.That(downtime.EndedUtc, Is.EqualTo(start.AddMinutes(4)));
            });
        }

        /// <summary>
        /// The blip is more recent than the outage but is not one, so the search walks past it to
        /// the last run that did reach the threshold rather than reporting the newest run there is.
        /// </summary>
        [Test]
        public async Task LoadAsync_PassesOverALaterBlipToTheLastRunThatReachedTheThreshold()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "flaky");
            // A real outage over minutes 1 to 3, then a single dropped ping at minute 5.
            await AddAttemptsAsync(db, host, start, [10, null, null, null, 20, null, 30]);

            var downtime = (await HostSummary.LoadAsync(db, TwoRetries)).Single().LastDowntime;

            Assert.Multiple(() =>
            {
                Assert.That(downtime!.StartedUtc, Is.EqualTo(start));
                Assert.That(downtime.EndedUtc, Is.EqualTo(start.AddMinutes(4)));
            });
        }

        /// <summary>
        /// The same walk, while the blip is still going: a host part way through a run that has not
        /// reached the threshold keeps showing the outage it last had, and shows it as over.
        /// </summary>
        [Test]
        public async Task LoadAsync_KeepsTheLastDowntimeClosedWhileAHostIsRetrying()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "flaky");
            await AddAttemptsAsync(db, host, start, [10, null, null, null, 20, null]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.Status, Is.EqualTo(HostStatus.Retrying));
                Assert.That(summary.LastDowntime!.StartedUtc, Is.EqualTo(start));
                Assert.That(summary.LastDowntime.EndedUtc, Is.EqualTo(start.AddMinutes(4)),
                    "the outage that ended at minute 4 is over; the ping missed since is not a new one");
            });
        }

        /// <summary>
        /// A host that has never answered has no answered ping to start its downtime from, and the
        /// threshold still has to be met before there is one to report.
        /// </summary>
        [TestCase(2, null, TestName = "LoadAsync_ReportsNoDowntimeForANeverAnsweredHostShortOfTheThreshold")]
        [TestCase(4, 0, TestName = "LoadAsync_StartsANeverAnsweredHostsDowntimeAtItsFirstAttempt")]
        public async Task LoadAsync_HoldsANeverAnsweredHostToTheThresholdToo(int misses, int? expectedStartMinute)
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "silent");
            await AddAttemptsAsync(db, host, start, [.. Enumerable.Repeat((int?)null, misses)]);

            var downtime = (await HostSummary.LoadAsync(db, TwoRetries)).Single().LastDowntime;

            Assert.That(
                downtime?.StartedUtc,
                Is.EqualTo(expectedStartMinute is int minute ? start.AddMinutes(minute) : (DateTime?)null));
        }

        /// <summary>Each host's downtime must be held to the threshold on its own attempts.</summary>
        [Test]
        public async Task LoadAsync_KeepsTheThresholdPerHost()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var first = await AddHostAsync(db, "first");
            var second = await AddHostAsync(db, "second");
            await AddAttemptsAsync(db, first, start, [10, null, null, null, 20]);
            await AddAttemptsAsync(db, second, start, [10, null, 20]);

            var summaries = await HostSummary.LoadAsync(db, TwoRetries);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].LastDowntime, Is.Not.Null);
                Assert.That(summaries[1].LastDowntime, Is.Null);
            });
        }

        [Test]
        public async Task LoadAsync_ReportsAHostThatIsAnsweringAsUp()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "healthy");
            await AddAttemptsAsync(db, host, start, [10, 20, 30]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.Status, Is.EqualTo(HostStatus.Up));
                Assert.That(summary.MissedPings, Is.Zero);
            });
        }

        [Test]
        public async Task LoadAsync_ReportsAHostThatIsNotBeingPingedAsPaused()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "parked");
            await AddAttemptsAsync(db, host, start, [null, null, null]);
            await db.Hosts.Where(h => h.Id == host.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsEnabled, false));

            var summary = (await HostSummary.LoadAsync(db, NoRetries)).Single();

            Assert.That(summary.Status, Is.EqualTo(HostStatus.Paused),
                "a host that is not being pinged is not a host that is known to be down");
        }

        [Test]
        public async Task LoadAsync_ReportsAHostNoRoundHasCoveredYetAsWaiting()
        {
            await using var db = new HostPingerDbContext(_options);
            await AddHostAsync(db, "fresh");

            var summary = (await HostSummary.LoadAsync(db, NoRetries)).Single();

            Assert.That(summary.Status, Is.EqualTo(HostStatus.Waiting));
        }

        /// <summary>
        /// The point of the threshold: a host stays retrying while it has missed fewer pings in a
        /// row than it takes to count as down, and only then goes down.
        /// </summary>
        [TestCase(1, HostStatus.Retrying, TestName = "LoadAsync_ReportsOneMissOfThreeAsRetrying")]
        [TestCase(2, HostStatus.Retrying, TestName = "LoadAsync_ReportsTwoMissesOfThreeAsRetrying")]
        [TestCase(3, HostStatus.Down, TestName = "LoadAsync_ReportsThreeMissesOfThreeAsDown")]
        [TestCase(4, HostStatus.Down, TestName = "LoadAsync_KeepsAHostDownPastTheThreshold")]
        public async Task LoadAsync_TurnsRetryingIntoDownAtTheThreshold(int misses, HostStatus expected)
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "flaky");
            await AddAttemptsAsync(db, host, start, [10, .. Enumerable.Repeat((int?)null, misses)]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.Status, Is.EqualTo(expected));
                Assert.That(summary.MissedPings, Is.EqualTo(Math.Min(misses, TwoRetries + 1)),
                    "the count stops at the threshold, which is as far as it is asked about");
            });
        }

        /// <summary>
        /// Allowing no retries is the behaviour the application had before the setting existed: the
        /// first missed ping is the host down, and nothing is ever reported as retrying.
        /// </summary>
        [Test]
        public async Task LoadAsync_ReportsTheFirstMissAsDownWhenNoRetriesAreAllowed()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "strict");
            await AddAttemptsAsync(db, host, start, [10, null]);

            var summary = (await HostSummary.LoadAsync(db, NoRetries)).Single();

            Assert.That(summary.Status, Is.EqualTo(HostStatus.Down));
        }

        /// <summary>
        /// A negative retry count is read as none rather than turned into a negative miss count,
        /// which would make a host that is answering look down.
        /// </summary>
        [TestCase(-1)]
        [TestCase(-5)]
        public async Task LoadAsync_ReadsANegativeRetryCountAsNone(int retryAttempts)
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var up = await AddHostAsync(db, "answering");
            var down = await AddHostAsync(db, "silent");
            await AddAttemptsAsync(db, up, start, [10, 20]);
            await AddAttemptsAsync(db, down, start, [10, null]);

            var summaries = await HostSummary.LoadAsync(db, retryAttempts);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].Status, Is.EqualTo(HostStatus.Up));
                Assert.That(summaries[1].Status, Is.EqualTo(HostStatus.Down));
            });
        }

        /// <summary>
        /// The run is what the host is missing now, not everything it has ever missed: an earlier
        /// outage sits behind an answered ping and must not push the current one over the
        /// threshold.
        /// </summary>
        [Test]
        public async Task LoadAsync_CountsOnlyTheMissesSinceTheLastAnsweredPing()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "flaky");
            await AddAttemptsAsync(db, host, start, [null, null, null, 10, null]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.MissedPings, Is.EqualTo(1));
                Assert.That(summary.Status, Is.EqualTo(HostStatus.Retrying));
            });
        }

        /// <summary>
        /// A host that has answered again is up, whatever it missed before that, and carries no
        /// misses to show against it.
        /// </summary>
        [Test]
        public async Task LoadAsync_ClearsTheMissesOnceTheHostAnswersAgain()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "recovered");
            await AddAttemptsAsync(db, host, start, [10, null, null, null, 20]);

            var summary = (await HostSummary.LoadAsync(db, TwoRetries)).Single();

            Assert.Multiple(() =>
            {
                Assert.That(summary.Status, Is.EqualTo(HostStatus.Up));
                Assert.That(summary.MissedPings, Is.Zero);
                Assert.That(summary.LastDowntime, Is.Not.Null,
                    "the outage still happened, and the Last downtime column still reports it");
            });
        }

        /// <summary>
        /// A host that has never answered has no answered ping to count from, so the run is every
        /// attempt it has.
        /// </summary>
        [Test]
        public async Task LoadAsync_CountsEveryAttemptForAHostThatNeverAnswered()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = await AddHostAsync(db, "silent");
            await AddAttemptsAsync(db, host, start, [null, null]);

            var summaries = await HostSummary.LoadAsync(db, TwoRetries);

            Assert.Multiple(() =>
            {
                Assert.That(summaries.Single().MissedPings, Is.EqualTo(2));
                Assert.That(summaries.Single().Status, Is.EqualTo(HostStatus.Retrying));
            });
        }

        /// <summary>Each host's run must come from its own attempts, not another host's.</summary>
        [Test]
        public async Task LoadAsync_KeepsMissedPingsPerHost()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var first = await AddHostAsync(db, "first");
            var second = await AddHostAsync(db, "second");
            await AddAttemptsAsync(db, first, start, [10, null, null]);
            await AddAttemptsAsync(db, second, start, [10, null]);

            var summaries = await HostSummary.LoadAsync(db, TwoRetries);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].MissedPings, Is.EqualTo(2));
                Assert.That(summaries[1].MissedPings, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// Guards the fix for the slow Hosts page. Projecting the last attempt as an entity makes EF
        /// emit ROW_NUMBER() OVER (PARTITION BY ...), which scans the whole PingAttempts table; the
        /// results stay correct, so only the shape of the SQL catches the regression.
        /// </summary>
        [Test]
        public void BuildQuery_DoesNotWindowOverTheWholeAttemptsTable()
        {
            using var db = new HostPingerDbContext(_options);

            var sql = HostSummary.BuildQuery(db, missesToDown: 1).ToQueryString();

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Not.Contain("ROW_NUMBER").IgnoreCase,
                    "The last attempt must be read with an indexed LIMIT 1, not a window over every attempt.");
                Assert.That(sql, Does.Contain("LIMIT 1"));
            });
        }

        /// <summary>
        /// The run of missed pings is counted no further than the threshold. Without the limit, a
        /// host that has been unreachable for a week costs a count of every ping it missed getting
        /// there — on every five-second refresh of the page, for every host that is down.
        /// </summary>
        [Test]
        public void BuildQuery_StopsCountingMissedPingsAtTheThreshold()
        {
            using var db = new HostPingerDbContext(_options);

            var sql = HostSummary.BuildQuery(db, missesToDown: 5).ToQueryString();

            Assert.That(sql, Does.Contain("LIMIT @missesToDown"));
        }

        /// <summary>
        /// The correlated subqueries are only fast because SQLite can walk
        /// IX_PingAttempts_HostId_TimestampUtc backwards; without the index it scans instead. The
        /// downtime lookups need IX_PingAttempts_Unanswered on top of it: the most recent
        /// unanswered ping is otherwise only reachable by walking back over every attempt answered
        /// since, which is unbounded for a host that stays up.
        /// </summary>
        [Test]
        public async Task BuildQuery_SeeksTheHostTimestampIndex()
        {
            using var db = new HostPingerDbContext(_options);

            await using var command = _connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + QueryText(db, missesToDown: 1);
            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(3));
                }
            }

            var detail = string.Join(" | ", plan);
            Assert.Multiple(() =>
            {
                Assert.That(detail, Does.Contain("IX_PingAttempts_HostId_TimestampUtc"),
                    $"Plan should seek the host/timestamp index. Plan was: {detail}");
                Assert.That(detail, Does.Contain("IX_PingAttempts_Unanswered"),
                    $"Plan should seek the unanswered-ping index. Plan was: {detail}");
                Assert.That(detail, Does.Not.Contain("SCAN PingAttempts"),
                    $"Plan should not scan the attempts table. Plan was: {detail}");
            });
        }

        /// <summary>
        /// The SQL the query runs, without the ".param set" lines ToQueryString writes in front of
        /// any query that carries a parameter. Those are sqlite3 shell commands rather than SQL,
        /// and SQLite refuses to prepare a statement starting with one. The placeholder is left
        /// unbound: what the threshold is bound to does not change which indexes the plan seeks.
        /// </summary>
        private static string QueryText(HostPingerDbContext db, int missesToDown)
        {
            var lines = HostSummary.BuildQuery(db, missesToDown).ToQueryString().Split('\n');
            return string.Join('\n', lines.SkipWhile(line => !line.StartsWith("SELECT", StringComparison.Ordinal)));
        }

        private static async Task<MonitoredHost> AddHostAsync(HostPingerDbContext db, string name)
        {
            var host = new MonitoredHost { Name = name, Address = $"{name}.example" };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            return host;
        }

        /// <summary>Records one attempt per minute from startUtc, one per round trip given.</summary>
        private static async Task AddAttemptsAsync(
            HostPingerDbContext db,
            MonitoredHost host,
            DateTime startUtc,
            IReadOnlyList<int?> roundtripsMs)
        {
            for (var minute = 0; minute < roundtripsMs.Count; minute++)
            {
                db.PingAttempts.Add(new PingAttempt
                {
                    HostId = host.Id,
                    TimestampUtc = startUtc.AddMinutes(minute),
                    RoundtripMs = roundtripsMs[minute],
                });
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
    }
}
