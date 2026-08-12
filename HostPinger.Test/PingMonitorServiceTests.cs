using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    public class PingMonitorServiceTests
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
        public async Task RunRound_RecordsRoundtripForUpHostsAndNullForDownHosts()
        {
            int upId, downId, pausedId;
            await using (var db = new HostPingerDbContext(_options))
            {
                var up = new MonitoredHost { Name = "up", Address = "up.example" };
                var down = new MonitoredHost { Name = "down", Address = "down.example" };
                var paused = new MonitoredHost { Name = "paused", Address = "paused.example", IsEnabled = false };
                db.Hosts.AddRange(up, down, paused);
                await db.SaveChangesAsync();
                (upId, downId, pausedId) = (up.Id, down.Id, paused.Id);
            }

            var sender = new FakePingSender { Results = { ["up.example"] = PingResult.Answered(42) } };
            var service = CreateService(sender);

            var recorded = await service.RunRoundAsync();

            Assert.That(recorded, Is.EqualTo(2));
            Assert.That(sender.Calls, Is.EquivalentTo(new[] { "up.example", "down.example" }));

            await using var verify = new HostPingerDbContext(_options);
            var attempts = await verify.PingAttempts.AsNoTracking().ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(attempts, Has.Count.EqualTo(2));
                Assert.That(attempts.Single(a => a.HostId == upId).RoundtripMs, Is.EqualTo(42));
                Assert.That(attempts.Single(a => a.HostId == downId).RoundtripMs, Is.Null);
                Assert.That(attempts.All(a => a.HostId != pausedId), "disabled hosts must not be pinged");
                Assert.That(attempts.All(a => Math.Abs((DateTime.UtcNow - a.TimestampUtc).TotalSeconds) < 30),
                    "timestamps should be current");
            });
        }

        [Test]
        public async Task RunRound_NoEnabledHosts_PingsNothing()
        {
            var sender = new FakePingSender();
            var service = CreateService(sender);

            var recorded = await service.RunRoundAsync();

            Assert.That(recorded, Is.Zero);
            Assert.That(sender.Calls, Is.Empty);
        }

        /// <summary>
        /// The timeout reaches the pinger from a file the user can edit, so a value that
        /// System.Net.NetworkInformation.Ping would reject must be clamped rather than thrown.
        /// </summary>
        [TestCase(3, 3_000)]
        [TestCase(0, 1_000)]
        [TestCase(-5, 1_000)]
        [TestCase(999_999, 60_000)]
        public async Task RunRound_ClampsTheConfiguredTimeout(int configuredSeconds, int expectedMilliseconds)
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.Add(new MonitoredHost { Name = "h", Address = "h.example" });
                await db.SaveChangesAsync();
            }

            var sender = new FakePingSender();
            var service = CreateService(sender, new PingerOptions { TimeoutSeconds = configuredSeconds });

            await service.RunRoundAsync();

            Assert.That(sender.Timeouts, Is.EqualTo(new[] { expectedMilliseconds }));
        }

        /// <summary>The resolve timeout is user-editable too, so it is clamped the same way.</summary>
        [TestCase(3, 3_000)]
        [TestCase(0, 1_000)]
        [TestCase(-5, 1_000)]
        [TestCase(999_999, 60_000)]
        public async Task RunRound_ClampsTheConfiguredResolveTimeout(int configuredSeconds, int expectedMilliseconds)
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.Add(new MonitoredHost { Name = "h", Address = "h.example" });
                await db.SaveChangesAsync();
            }

            var sender = new FakePingSender();
            var service = CreateService(sender, new PingerOptions { ResolveTimeoutSeconds = configuredSeconds });

            await service.RunRoundAsync();

            Assert.That(sender.ResolveTimeouts, Is.EqualTo(new[] { expectedMilliseconds }));
        }

        /// <summary>Pins the documented default, which nothing else in the wiring states.</summary>
        [Test]
        public async Task RunRound_ResolveTimeoutDefaultsToThreeSeconds()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.Add(new MonitoredHost { Name = "h", Address = "h.example" });
                await db.SaveChangesAsync();
            }

            var sender = new FakePingSender();
            var service = CreateService(sender);

            await service.RunRoundAsync();

            Assert.That(sender.ResolveTimeouts, Is.EqualTo(new[] { 3_000 }));
        }

        /// <summary>
        /// An unresolved address means nothing was ever asked of the host, so the round stores no
        /// attempt for it. Storing it as unanswered would be read as downtime later — an outage
        /// invented out of a name that does not resolve. A host that was asked and stayed silent is
        /// still a missed ping, and the difference between the two is the point. What the round does
        /// record for it is a resolver error; see
        /// <see cref="RunRound_RecordsAFailedLookupAgainstTheAddress"/>.
        /// </summary>
        [Test]
        public async Task RunRound_StoresNoPingAttemptForAHostThatDidNotResolve()
        {
            int answeredId, silentId, unresolvedId;
            await using (var db = new HostPingerDbContext(_options))
            {
                var answered = new MonitoredHost { Name = "answered", Address = "answered.example" };
                var silent = new MonitoredHost { Name = "silent", Address = "silent.example" };
                var unresolved = new MonitoredHost { Name = "unresolved", Address = "unresolved.example" };
                db.Hosts.AddRange(answered, silent, unresolved);
                await db.SaveChangesAsync();
                (answeredId, silentId, unresolvedId) = (answered.Id, silent.Id, unresolved.Id);
            }

            var sender = new FakePingSender
            {
                Results =
                {
                    ["answered.example"] = PingResult.Answered(7),
                    ["silent.example"] = PingResult.Unanswered,
                    ["unresolved.example"] = PingResult.Unresolved(ResolverFailure.LookupFailed),
                },
            };
            var service = CreateService(sender);

            var recorded = await service.RunRoundAsync();

            Assert.That(recorded, Is.EqualTo(2));

            await using var verify = new HostPingerDbContext(_options);
            var attempts = await verify.PingAttempts.AsNoTracking().ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(attempts.Select(a => a.HostId), Is.EquivalentTo(new[] { answeredId, silentId }));
                Assert.That(attempts.Single(a => a.HostId == answeredId).RoundtripMs, Is.EqualTo(7));
                Assert.That(attempts.Single(a => a.HostId == silentId).RoundtripMs, Is.Null,
                    "a host that was asked and stayed silent is still a missed ping");
                Assert.That(attempts.All(a => a.HostId != unresolvedId),
                    "the unresolved host must not appear in the ping history at all");
                Assert.That(sender.Calls, Contains.Item("unresolved.example"),
                    "it should still be tried next round, not dropped from the rotation");
            });
        }

        /// <summary>
        /// The other half of that: the round the host was left out of says why, against the address
        /// that would not resolve and with the reason the lookup failed. Only the addresses that
        /// failed to resolve are recorded — a host that answered, and one that was asked and stayed
        /// silent, both resolved perfectly well.
        /// </summary>
        [Test]
        public async Task RunRound_RecordsAFailedLookupAgainstTheAddress()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.AddRange(
                    new MonitoredHost { Name = "answered", Address = "answered.example" },
                    new MonitoredHost { Name = "silent", Address = "silent.example" },
                    new MonitoredHost { Name = "slow name", Address = "slow.example" },
                    new MonitoredHost { Name = "unknown name", Address = "unknown.example" });
                await db.SaveChangesAsync();
            }

            var sender = new FakePingSender
            {
                Results =
                {
                    ["answered.example"] = PingResult.Answered(7),
                    ["silent.example"] = PingResult.Unanswered,
                    ["slow.example"] = PingResult.Unresolved(ResolverFailure.TimedOut),
                    ["unknown.example"] = PingResult.Unresolved(ResolverFailure.LookupFailed),
                },
            };

            await CreateService(sender).RunRoundAsync();

            await using var verify = new HostPingerDbContext(_options);
            var errors = await verify.ResolverErrors.AsNoTracking().ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(errors.Select(e => e.Address),
                    Is.EquivalentTo(new[] { "slow.example", "unknown.example" }));
                Assert.That(errors.Single(e => e.Address == "slow.example").Reason,
                    Is.EqualTo(ResolverFailure.TimedOut));
                Assert.That(errors.Single(e => e.Address == "unknown.example").Reason,
                    Is.EqualTo(ResolverFailure.LookupFailed));
                Assert.That(errors.All(e => Math.Abs((DateTime.UtcNow - e.TimestampUtc).TotalSeconds) < 30),
                    "timestamps should be the round's own");
            });
        }

        /// <summary>
        /// The round is where the retention is applied, and it is applied whether or not the round
        /// had anything to ping: a service whose hosts have all been deleted or paused still holds
        /// the failures they left behind, and those age out like any others.
        /// </summary>
        [Test]
        public async Task RunRound_DropsResolverErrorsPastTheRetentionEvenWithNothingToPing()
        {
            var now = DateTime.UtcNow;
            await using (var db = new HostPingerDbContext(_options))
            {
                db.ResolverErrors.AddRange(
                    new ResolverError
                    {
                        Address = "gone.example",
                        TimestampUtc = now - ResolverError.Retention - TimeSpan.FromDays(1),
                        Reason = ResolverFailure.LookupFailed,
                    },
                    new ResolverError
                    {
                        Address = "gone.example",
                        TimestampUtc = now.AddDays(-1),
                        Reason = ResolverFailure.LookupFailed,
                    });
                await db.SaveChangesAsync();
            }

            var recorded = await CreateService(new FakePingSender()).RunRoundAsync();

            Assert.That(recorded, Is.Zero, "there were no hosts to ping");

            await using var verify = new HostPingerDbContext(_options);
            var remaining = await verify.ResolverErrors.AsNoTracking().ToListAsync();
            Assert.That(remaining.Select(e => e.TimestampUtc), Is.EqualTo(new[] { now.AddDays(-1) }));
        }

        /// <summary>
        /// A round that takes time has to be absorbed by the interval, not added to it. Assigning
        /// <c>PeriodicTimer.Period</c> restarts its countdown from that moment, so assigning it
        /// after every round put each round's duration into the gap and left the real cadence at
        /// interval plus round.
        /// </summary>
        /// <remarks>
        /// Runs on the real clock, at the shortest interval <see cref="PingMonitorService"/> will
        /// accept, because the bug is only visible when a round consumes time the timer can be
        /// pushed by. A FakeTimeProvider cannot stand in here: the loop resumes inline inside
        /// <c>Advance</c>, so a round that moved the fake clock would shift the timer's own due
        /// time and report the drift even against a correct implementation. The assertion is
        /// one-sided for that reason — scheduling jitter can only widen a gap, so a gap that stays
        /// below interval-plus-most-of-a-round is proof the duration was absorbed.
        /// </remarks>
        [Test]
        public async Task ExecuteAsync_ASlowRoundDoesNotStretchTheInterval()
        {
            var interval = TimeSpan.FromSeconds(1);
            var roundDuration = TimeSpan.FromMilliseconds(800);
            const int expectedRounds = 4;

            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.Add(new MonitoredHost { Name = "slow", Address = "slow.example" });
                await db.SaveChangesAsync();
            }

            // One host, so one ping per round, and the ping is what makes the round take time —
            // exactly how a host that answers slowly or times out behaves.
            var sender = new FakePingSender { Delay = roundDuration, Results = { ["slow.example"] = PingResult.Answered(1) } };
            var service = CreateService(sender, new PingerOptions { IntervalSeconds = (int)interval.TotalSeconds });

            await service.StartAsync(CancellationToken.None);
            try
            {
                // Waits for one round beyond the ones being measured: a ping is counted when it
                // starts, so the round it belongs to has only certainly been written once the
                // next one has begun. Stopping any earlier cancels a round mid-ping and loses it.
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (sender.CallCount <= expectedRounds && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            await using var verify = new HostPingerDbContext(_options);
            var roundStarts = await verify.PingAttempts.AsNoTracking()
                .OrderBy(a => a.TimestampUtc)
                .Select(a => a.TimestampUtc)
                .ToListAsync();

            Assert.That(roundStarts, Has.Count.AtLeast(expectedRounds), "the loop should have run several rounds");

            var gaps = roundStarts.Zip(roundStarts.Skip(1), (earlier, later) => later - earlier).ToList();
            var stretched = interval + (roundDuration / 2);
            Assert.That(
                gaps,
                Is.All.LessThan(stretched),
                $"a {roundDuration.TotalMilliseconds}ms round must not be added to a "
                    + $"{interval.TotalSeconds}s interval; gaps were "
                    + string.Join(", ", gaps.Select(gap => $"{gap.TotalMilliseconds:F0}ms")));
        }

        private PingMonitorService CreateService(IPingSender sender, PingerOptions? options = null)
        {
            return new PingMonitorService(
                new TestDb.Factory(_options),
                sender,
                new TestOptionsMonitor<PingerOptions>(options ?? new PingerOptions()),
                new DatabasePruner(),
                NullLogger<PingMonitorService>.Instance);
        }

        private sealed class FakePingSender : IPingSender
        {
            /// <summary>Outcome per address. An address that is absent goes unanswered.</summary>
            public Dictionary<string, PingResult> Results { get; } = [];

            public List<string> Calls { get; } = [];

            public List<int> Timeouts { get; } = [];

            public List<int> ResolveTimeouts { get; } = [];

            /// <summary>How long each ping takes, which is what gives a round its duration.</summary>
            public TimeSpan Delay { get; init; }

            /// <summary>Safe to read while the monitor loop is still pinging.</summary>
            public int CallCount
            {
                get
                {
                    lock (Calls)
                    {
                        return Calls.Count;
                    }
                }
            }

            public async Task<PingResult> SendPingAsync(
                string address,
                int timeoutMilliseconds,
                int resolveTimeoutMilliseconds,
                CancellationToken cancellationToken = default)
            {
                lock (Calls)
                {
                    Calls.Add(address);
                    Timeouts.Add(timeoutMilliseconds);
                    ResolveTimeouts.Add(resolveTimeoutMilliseconds);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                return Results.GetValueOrDefault(address, PingResult.Unanswered);
            }
        }
    }
}
