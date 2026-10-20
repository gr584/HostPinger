using System.Data.Common;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
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

        /// <summary>
        /// A host deleted while the round is pinging it costs the round that host's attempt and
        /// nothing more. The round read its hosts before the pings, which take seconds, and the
        /// Hosts page does not wait for it to finish; saving an attempt for a host that has gone
        /// would have the foreign key refuse the whole save, and every other host's ping with it.
        /// </summary>
        [Test]
        public async Task RunRound_AHostDeletedWhileItIsPinged_LosesOnlyItsOwnAttempt()
        {
            int keptId;
            await using (var db = new HostPingerDbContext(_options))
            {
                var kept = new MonitoredHost { Name = "kept", Address = "kept.example" };
                var deleted = new MonitoredHost { Name = "deleted", Address = "deleted.example" };
                db.Hosts.AddRange(kept, deleted);
                await db.SaveChangesAsync();
                keptId = kept.Id;
            }

            var sender = new FakePingSender
            {
                Results = { ["kept.example"] = PingResult.Answered(3), ["deleted.example"] = PingResult.Answered(4) },
                WhilePinging = async address =>
                {
                    if (address == "deleted.example")
                    {
                        await using var db = new HostPingerDbContext(_options);
                        await db.Hosts.Where(h => h.Address == address).ExecuteDeleteAsync();
                    }
                },
            };

            var recorded = await CreateService(sender).RunRoundAsync();

            await using var verify = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(recorded, Is.EqualTo(1));
                Assert.That(await verify.PingAttempts.Select(a => a.HostId).ToListAsync(), Is.EqualTo(new[] { keptId }));
            });
        }

        /// <summary>
        /// A host paused while the round is pinging it keeps the attempt it was pinged for: it was
        /// asked, and what it answered is history like any other attempt. Only a host that has gone
        /// has nothing for its attempt to point at.
        /// </summary>
        [Test]
        public async Task RunRound_AHostPausedWhileItIsPinged_KeepsItsAttempt()
        {
            int pausedId;
            await using (var db = new HostPingerDbContext(_options))
            {
                var paused = new MonitoredHost { Name = "paused", Address = "paused.example" };
                db.Hosts.Add(paused);
                await db.SaveChangesAsync();
                pausedId = paused.Id;
            }

            var sender = new FakePingSender
            {
                Results = { ["paused.example"] = PingResult.Answered(4) },
                WhilePinging = async address =>
                {
                    await using var db = new HostPingerDbContext(_options);
                    await db.Hosts.Where(h => h.Address == address)
                        .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsEnabled, false));
                },
            };

            var recorded = await CreateService(sender).RunRoundAsync();

            await using var verify = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(recorded, Is.EqualTo(1));
                Assert.That(await verify.PingAttempts.Select(a => a.HostId).ToListAsync(), Is.EqualTo(new[] { pausedId }));
            });
        }

        /// <summary>
        /// The round asks which hosts are still there under the same write lock it saves with, so
        /// that a delete coming between the asking and the saving waits for the round rather than
        /// landing in between, where it would have the foreign key refuse the save just the same.
        /// The delete is tried the moment the answer comes back, from a connection of its own that
        /// gives up after a second. In WAL mode, the one a database this application creates starts
        /// in, a read keeps no writer out, so only the round's write lock can: asked outside the
        /// transaction, or inside one that takes the lock only when it first writes, the delete
        /// lands and the save fails.
        /// </summary>
        [Test]
        public async Task RunRound_AsksWhichHostsRemainUnderTheLockItSavesWith()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hostpinger-round-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "hostpinger.db");
                var options = new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite($"Data Source={path}").Options;
                int keptId, deletedId;
                await using (var db = new HostPingerDbContext(options))
                {
                    await db.Database.EnsureCreatedAsync();
                    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
                    var kept = new MonitoredHost { Name = "kept", Address = "kept.example" };
                    var deleted = new MonitoredHost { Name = "deleted", Address = "deleted.example" };
                    db.Hosts.AddRange(kept, deleted);
                    await db.SaveChangesAsync();
                    (keptId, deletedId) = (kept.Id, deleted.Id);
                }

                // Armed by the pings, so the read it goes off after is the first one past them: the
                // round's question about which hosts remain, not its reading of the hosts to ping.
                var armed = false;
                bool? refused = null;
                var afterRead = new AfterReadInterceptor(() =>
                {
                    if (!armed)
                    {
                        return;
                    }

                    armed = false;
                    using var other = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=1");
                    other.Open();
                    using var delete = other.CreateCommand();
                    delete.CommandText = $"DELETE FROM Hosts WHERE Id = {deletedId};";
                    try
                    {
                        delete.ExecuteNonQuery();
                        refused = false;
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
                    {
                        refused = true;
                    }
                });
                var roundOptions = new DbContextOptionsBuilder<HostPingerDbContext>()
                    .UseSqlite($"Data Source={path}")
                    .AddInterceptors(afterRead)
                    .Options;
                var sender = new FakePingSender
                {
                    WhilePinging = _ =>
                    {
                        armed = true;
                        return Task.CompletedTask;
                    },
                };

                var recorded = await CreateService(sender, dbFactory: new TestDb.Factory(roundOptions)).RunRoundAsync();

                await using var verify = new HostPingerDbContext(options);
                Assert.Multiple(async () =>
                {
                    Assert.That(refused, Is.True, "the delete was kept out until the round had saved");
                    Assert.That(recorded, Is.EqualTo(2));
                    Assert.That(
                        await verify.PingAttempts.Select(a => a.HostId).OrderBy(id => id).ToListAsync(),
                        Is.EqualTo(new[] { keptId, deletedId }));
                });
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// A round must not write while a backup or restore holds the maintenance gate — that is
        /// the whole contract that lets a restore swap the file under a running monitor.
        /// </summary>
        [Test]
        public async Task RunRound_WaitsWhileMaintenanceHoldsTheGate()
        {
            var gate = new MaintenanceGate();
            var service = CreateService(new FakePingSender(), maintenanceGate: gate);

            var hold = await gate.AcquireAsync();
            var round = service.RunRoundAsync();

            // Long enough that a round which ignored the gate would have finished several times
            // over — there are no hosts, so an unblocked round is nearly instant.
            await Task.Delay(200);
            Assert.That(round.IsCompleted, Is.False, "the round must wait for the gate");

            hold.Dispose();
            Assert.That(await round, Is.Zero, "released, the round runs to its usual end");
        }

        /// <summary>
        /// A round that fails is logged and the loop goes on: the next round runs on time and
        /// records as usual. A loop that let the failure out would stop monitoring for good on one
        /// bad round — a database locked past its wait, a resolver throwing.
        /// </summary>
        [Test]
        public async Task ExecuteAsync_LogsAFailedRoundAndGoesOnToTheNext()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.Add(new MonitoredHost { Name = "up", Address = "up.example" });
                await db.SaveChangesAsync();
            }

            var sender = new FakePingSender { FailFirstCalls = 1, Results = { ["up.example"] = PingResult.Answered(5) } };
            var logger = new TestLogger<PingMonitorService>();
            var service = CreateService(sender, new PingerOptions { IntervalSeconds = 1 }, logger: logger);

            await service.StartAsync(CancellationToken.None);
            try
            {
                // Waited on through the pinger rather than the database, whose one in-memory
                // connection the loop is using: a query from here could land inside a round's
                // transaction. A ping is counted as it starts, so the round after the failure has
                // certainly been written once the one after that has begun.
                await WaitUntilAsync(() => Task.FromResult(sender.CallCount >= 3));
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            await using var verify = new HostPingerDbContext(_options);
            var failures = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
            Assert.Multiple(async () =>
            {
                Assert.That(failures, Has.Count.EqualTo(1));
                Assert.That(failures[0].Exception, Is.InstanceOf<InvalidOperationException>());
                Assert.That(await verify.PingAttempts.AnyAsync(), Is.True, "the round after the failure recorded");
            });
        }

        /// <summary>
        /// An interval saved on the Configuration page while the loop runs is taken up from the
        /// round after next: here a one-second loop is told to wait a day, and the rounds stop.
        /// </summary>
        [Test]
        public async Task ExecuteAsync_TakesUpAnIntervalChangedWhileItRuns()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.Hosts.Add(new MonitoredHost { Name = "up", Address = "up.example" });
                await db.SaveChangesAsync();
            }

            var sender = new FakePingSender { Results = { ["up.example"] = PingResult.Answered(5) } };
            var pinger = new TestOptionsMonitor<PingerOptions>(new PingerOptions { IntervalSeconds = 1 });
            var service = CreateService(sender, pinger: pinger);

            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => Task.FromResult(sender.CallCount >= 2));
                pinger.CurrentValue = new PingerOptions { IntervalSeconds = 86_400 };

                // The tick already counting down at the old period still comes, and the round it
                // starts is the one that reads the new interval.
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                var settled = sender.CallCount;
                await Task.Delay(TimeSpan.FromSeconds(2.5));

                Assert.That(sender.CallCount, Is.EqualTo(settled), "no round at the old one-second interval");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await condition())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "gave up waiting");
                await Task.Delay(25);
            }
        }

        private PingMonitorService CreateService(
            IPingSender sender,
            PingerOptions? options = null,
            MaintenanceGate? maintenanceGate = null,
            TestOptionsMonitor<PingerOptions>? pinger = null,
            ILogger<PingMonitorService>? logger = null,
            IDbContextFactory<HostPingerDbContext>? dbFactory = null)
        {
            // The settings arrive as configured defaults under a store with no rows, which is the
            // state of a fresh install.
            return new PingMonitorService(
                dbFactory ?? new TestDb.Factory(_options),
                sender,
                new UserSettingsStore(
                    new TestDb.Factory(_options),
                    pinger ?? new TestOptionsMonitor<PingerOptions>(options ?? new PingerOptions()),
                    new TestOptionsMonitor<SecurityOptions>(new SecurityOptions())),
                new DatabasePruner(),
                maintenanceGate ?? new MaintenanceGate(),
                logger ?? NullLogger<PingMonitorService>.Instance);
        }

        /// <summary>
        /// Runs <paramref name="afterRead"/> each time a query's reader comes back, before its rows
        /// are read: the moment between a question and whatever the round does with the answer.
        /// </summary>
        private sealed class AfterReadInterceptor(Action afterRead) : DbCommandInterceptor
        {
            public override DbDataReader ReaderExecuted(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result)
            {
                afterRead();
                return result;
            }

            public override ValueTask<DbDataReader> ReaderExecutedAsync(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result,
                CancellationToken cancellationToken = default)
            {
                afterRead();
                return ValueTask.FromResult(result);
            }
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

            /// <summary>How many of the first pings throw, standing in for a round that fails.</summary>
            public int FailFirstCalls { get; init; }

            /// <summary>
            /// Run with each address while it is being pinged, after the round has read its hosts
            /// and before it saves anything: what happens to the database during a round.
            /// </summary>
            public Func<string, Task>? WhilePinging { get; init; }

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
                bool fail;
                lock (Calls)
                {
                    Calls.Add(address);
                    Timeouts.Add(timeoutMilliseconds);
                    ResolveTimeouts.Add(resolveTimeoutMilliseconds);
                    fail = Calls.Count <= FailFirstCalls;
                }

                if (fail)
                {
                    throw new InvalidOperationException("A round that fails.");
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (WhilePinging is { } whilePinging)
                {
                    await whilePinging(address);
                }

                return Results.GetValueOrDefault(address, PingResult.Unanswered);
            }
        }
    }
}
