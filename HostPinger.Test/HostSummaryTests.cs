using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class HostSummaryTests
    {
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

            var summaries = await HostSummary.LoadAsync(db);

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

            var summaries = await HostSummary.LoadAsync(db);

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

            var summaries = await HostSummary.LoadAsync(db);

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

            var summaries = await HostSummary.LoadAsync(db);

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

            var downtime = (await HostSummary.LoadAsync(db)).Single().LastDowntime;

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

            var downtime = (await HostSummary.LoadAsync(db)).Single().LastDowntime;

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

            var downtime = (await HostSummary.LoadAsync(db)).Single().LastDowntime;

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

            var downtime = (await HostSummary.LoadAsync(db)).Single().LastDowntime;

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

            var summaries = await HostSummary.LoadAsync(db);

            Assert.That(summaries.Single().LastDowntime, Is.Null);
        }

        [Test]
        public async Task LoadAsync_LeavesTheDowntimeNullForAHostThatWasNeverPinged()
        {
            await using var db = new HostPingerDbContext(_options);
            await AddHostAsync(db, "fresh");

            var summaries = await HostSummary.LoadAsync(db);

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

            var summaries = await HostSummary.LoadAsync(db);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].LastDowntime!.StartedUtc, Is.EqualTo(start));
                Assert.That(summaries[0].LastDowntime!.EndedUtc, Is.EqualTo(start.AddMinutes(2)));
                Assert.That(summaries[1].LastDowntime, Is.Null);
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

            var sql = HostSummary.BuildQuery(db).ToQueryString();

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Not.Contain("ROW_NUMBER").IgnoreCase,
                    "The last attempt must be read with an indexed LIMIT 1, not a window over every attempt.");
                Assert.That(sql, Does.Contain("LIMIT 1"));
            });
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
            var sql = HostSummary.BuildQuery(db).ToQueryString();

            await using var command = _connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
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
