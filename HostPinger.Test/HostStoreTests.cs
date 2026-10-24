using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    /// <summary>
    /// The changes the Hosts page makes to the hosts, and the summary each hands back. Against a
    /// real file, because the summary only notices a change made on a connection of its own.
    /// </summary>
    /// <remarks>
    /// The summary's loop is not started here: each test takes the read a change is waiting for
    /// itself, with <see cref="HostSummaryCache.PollAsync"/>, once the change has been made. A loop
    /// running on its own could read in between and hand a store that never asked a summary that
    /// happens to hold the change.
    /// </remarks>
    public class HostStoreTests
    {
        private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private string _directory = null!;
        private string _databasePath = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;
        private UserSettingsStore _settings = null!;
        private HostSummaryCache _summaries = null!;
        private HostStore _store = null!;

        /// <summary>The ways a host is changed, for what holds of all of them.</summary>
        public enum Change
        {
            Add,
            Update,
            SetEnabled,
            Delete,
        }

        [SetUp]
        public async Task SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "hostpinger.db");
            _options = Options($"Data Source={_databasePath}");
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            _settings = new UserSettingsStore(
                new TestDb.Factory(_options),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions { RetryAttempts = 3 }),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
            _summaries = new HostSummaryCache(new TestDb.Factory(_options), _settings, NullLogger<HostSummaryCache>.Instance);
            await _summaries.LoadAsync();
            _store = Store(_options);
        }

        [TearDown]
        public void TearDown()
        {
            _summaries.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        /// <summary>
        /// A new host is in the summary the store hands back, which the store waited on rather than
        /// handing back the one in hand: the read is taken after the change, so the table the page
        /// draws next already lists it.
        /// </summary>
        [Test]
        public async Task AddAsync_HandsBackASummaryThatAlreadyListsTheHost()
        {
            var change = await MakeAsync(() => _store.AddAsync("alpha", "alpha.example", isEnabled: true));

            Assert.Multiple(() =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.Saved));
                Assert.That(Names(change.Summary), Is.EqualTo(new[] { "alpha" }));
                Assert.That(change.Summary.Rows[0].Status, Is.EqualTo(HostStatus.Waiting), "never pinged yet");
                Assert.That(change.Summary, Is.SameAs(_summaries.Current));
            });
        }

        /// <summary>
        /// What is stored: the name and the address without the blanks typed around them, the
        /// switch as it was set, and the moment the host was added.
        /// </summary>
        [Test]
        public async Task AddAsync_StoresTheHostTrimmedAndStampedWithItsCreation()
        {
            await MakeAsync(() => _store.AddAsync("  alpha ", " alpha.example  ", isEnabled: false));

            var host = (await HostsAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(host.Name, Is.EqualTo("alpha"));
                Assert.That(host.Address, Is.EqualTo("alpha.example"));
                Assert.That(host.IsEnabled, Is.False);
                Assert.That(host.CreatedUtc, Is.EqualTo(Now));
            });
        }

        /// <summary>
        /// An address another host already has is refused and nothing is written. Trimmed first,
        /// so a space in front of it does not make it a different address. The refusal comes back
        /// at once, with the summary in hand, since there is no change to wait to read.
        /// </summary>
        [Test]
        public async Task AddAsync_RefusesAnAddressAnotherHostHas()
        {
            await MakeAsync(() => _store.AddAsync("first", "taken.example", isEnabled: true));
            var inHand = _summaries.Current;

            var adding = _store.AddAsync("second", " taken.example ", isEnabled: true);

            Assert.That(adding.IsCompleted, Is.True, "nothing to wait for");
            var change = await adding;
            Assert.Multiple(async () =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.AddressTaken));
                Assert.That(change.Summary, Is.SameAs(inHand));
                Assert.That((await HostsAsync()).Select(h => h.Name), Is.EqualTo(new[] { "first" }));
            });
        }

        /// <summary>
        /// A name or an address that is nothing but blanks, or longer than its column, is refused
        /// before anything is written, whether a host is being added or edited. The Hosts page's
        /// form refuses the same, so this holds for any other caller: SQLite would store either,
        /// and an empty address resolves as this machine and reads as up.
        /// </summary>
        [TestCaseSource(nameof(Unstorable))]
        public async Task Changes_RefuseANameOrAddressAHostCannotHave(Change change, string name, string address, string parameter)
        {
            var id = await AddHostAsync("alpha", "alpha.example");

            // Bounded, because a store that took the value would then wait for a read nothing here
            // takes, and that should fail the test rather than hang the run.
            var failure = await Assert.ThrowsAsync<ArgumentException>(() => (change == Change.Add
                ? _store.AddAsync(name, address, isEnabled: true)
                : _store.UpdateAsync(id, name, address, isEnabled: true)).WaitAsync(Patience));

            var host = (await HostsAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(failure!.ParamName, Is.EqualTo(parameter));
                Assert.That(host.Name, Is.EqualTo("alpha"));
                Assert.That(host.Address, Is.EqualTo("alpha.example"));
            });
        }

        /// <summary>
        /// The bound is on what is stored, so a name and an address as long as their columns hold
        /// are taken with blanks around them, which are trimmed off.
        /// </summary>
        [Test]
        public async Task AddAsync_TakesANameAndAddressAsLongAsAHostsCanBe()
        {
            var name = new string('n', MonitoredHost.MaxNameLength);
            var address = new string('a', MonitoredHost.MaxAddressLength);

            await MakeAsync(() => _store.AddAsync($" {name} ", $"\t{address} ", isEnabled: true));

            var host = (await HostsAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(host.Name, Is.EqualTo(name));
                Assert.That(host.Address, Is.EqualTo(address));
            });
        }

        private static IEnumerable<TestCaseData> Unstorable()
        {
            foreach (var change in new[] { Change.Add, Change.Update })
            {
                yield return new TestCaseData(change, "  ", "omega.example", "name").SetArgDisplayNames(change.ToString(), "blank name");
                yield return new TestCaseData(change, "omega", " \t ", "address").SetArgDisplayNames(change.ToString(), "blank address");
                yield return new TestCaseData(change, new string('n', MonitoredHost.MaxNameLength + 1), "omega.example", "name")
                    .SetArgDisplayNames(change.ToString(), "long name");
                yield return new TestCaseData(change, "omega", new string('a', MonitoredHost.MaxAddressLength + 1), "address")
                    .SetArgDisplayNames(change.ToString(), "long address");
            }
        }

        /// <summary>An edit is in the summary handed back, as an addition is.</summary>
        [Test]
        public async Task UpdateAsync_HandsBackASummaryThatAlreadyHoldsTheEdit()
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            await AddHostAsync("zulu", "zulu.example");

            var change = await MakeAsync(() => _store.UpdateAsync(id, " renamed ", " moved.example ", isEnabled: false));

            Assert.That(
                (await HostsAsync()).Single(h => h.Id != id),
                Has.Property(nameof(MonitoredHost.Name)).EqualTo("zulu")
                    .And.Property(nameof(MonitoredHost.Address)).EqualTo("zulu.example")
                    .And.Property(nameof(MonitoredHost.IsEnabled)).True,
                "the other host is left as it was");
            var row = change.Summary.Rows.Single(r => r.Host.Id == id);
            Assert.Multiple(() =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.Saved));
                Assert.That(row.Host.Id, Is.EqualTo(id));
                Assert.That(row.Host.Name, Is.EqualTo("renamed"));
                Assert.That(row.Host.Address, Is.EqualTo("moved.example"));
                Assert.That(row.Status, Is.EqualTo(HostStatus.Paused));
            });
        }

        /// <summary>
        /// Keeping its own address is not taking another host's: a rename alone saves, which a check
        /// for the address being anywhere in the table would refuse.
        /// </summary>
        [Test]
        public async Task UpdateAsync_KeepsTheHostsOwnAddress()
        {
            var id = await AddHostAsync("alpha", "alpha.example");

            var change = await MakeAsync(() => _store.UpdateAsync(id, "renamed", "alpha.example", isEnabled: true));

            Assert.Multiple(() =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.Saved));
                Assert.That(Names(change.Summary), Is.EqualTo(new[] { "renamed" }));
            });
        }

        /// <summary>Moving a host onto another host's address is refused, and the host is left as it was.</summary>
        [Test]
        public async Task UpdateAsync_RefusesAnAddressAnotherHostHas()
        {
            await AddHostAsync("first", "taken.example");
            var id = await AddHostAsync("second", "second.example");
            var inHand = _summaries.Current;

            var updating = _store.UpdateAsync(id, "second", "taken.example ", isEnabled: false);

            Assert.That(updating.IsCompleted, Is.True, "nothing to wait for");
            var change = await updating;

            var second = (await HostsAsync()).Single(h => h.Id == id);
            Assert.Multiple(() =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.AddressTaken));
                Assert.That(change.Summary, Is.SameAs(inHand));
                Assert.That(second.Address, Is.EqualTo("second.example"));
                Assert.That(second.IsEnabled, Is.True);
            });
        }

        /// <summary>
        /// A host deleted while its edit was open is reported as gone, and the edit does not bring
        /// it back or touch any other host.
        /// </summary>
        [Test]
        public async Task UpdateAsync_ReportsAHostThatIsNoLongerThere()
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            var inHand = _summaries.Current;

            var updating = _store.UpdateAsync(id + 1, "ghost", "ghost.example", isEnabled: true);

            Assert.That(updating.IsCompleted, Is.True, "nothing to wait for");
            var change = await updating;

            Assert.Multiple(async () =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.NotFound));
                Assert.That(change.Summary, Is.SameAs(inHand));
                Assert.That((await HostsAsync()).Select(h => h.Name), Is.EqualTo(new[] { "alpha" }));
            });
        }

        /// <summary>
        /// The switch: a paused host is in the summary handed back as paused, and a resumed one as
        /// whatever its pings say, without waiting for the summary's next turn either way.
        /// </summary>
        [Test]
        public async Task SetEnabledAsync_HandsBackASummaryWithTheHostPausedAndThenResumed()
        {
            var id = await AddHostAsync("alpha", "alpha.example", roundtripMs: 12);

            var paused = await MakeAsync(() => _store.SetEnabledAsync(id, isEnabled: false));
            var resumed = await MakeAsync(() => _store.SetEnabledAsync(id, isEnabled: true));

            Assert.Multiple(() =>
            {
                Assert.That(paused.Rows.Single().Status, Is.EqualTo(HostStatus.Paused));
                Assert.That(resumed.Rows.Single().Status, Is.EqualTo(HostStatus.Up));
            });
        }

        /// <summary>Pausing one host leaves the others as they were.</summary>
        [Test]
        public async Task SetEnabledAsync_PausesOnlyThatHost()
        {
            var id = await AddHostAsync("alpha", "alpha.example", roundtripMs: 12);
            await AddHostAsync("bravo", "bravo.example", roundtripMs: 15);

            var summary = await MakeAsync(() => _store.SetEnabledAsync(id, isEnabled: false));

            Assert.That(
                summary.Rows.Select(r => (r.Host.Name, r.Status)),
                Is.EqualTo(new[] { ("alpha", HostStatus.Paused), ("bravo", HostStatus.Up) }));
        }

        /// <summary>
        /// A deleted host is gone from the summary handed back, and its ping history is gone with
        /// it, which is what the confirmation on the page promises. The other hosts stay.
        /// </summary>
        [Test]
        public async Task DeleteAsync_HandsBackASummaryWithoutTheHostAndTakesItsHistory()
        {
            var id = await AddHostAsync("alpha", "alpha.example", roundtripMs: 12);
            var kept = await AddHostAsync("bravo", "bravo.example", roundtripMs: 15);

            var summary = await MakeAsync(() => _store.DeleteAsync(id));

            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(Names(summary), Is.EqualTo(new[] { "bravo" }));
                Assert.That(await db.PingAttempts.Select(a => a.HostId).ToListAsync(), Is.EqualTo(new[] { kept }));
            });
        }

        /// <summary>
        /// A database that will not take a change — here because another connection holds the write
        /// lock for longer than the provider waits — is reported as the change not being saved, and
        /// never as the address being taken, which is how the page used to read every failure of a
        /// save. Nothing is written, so there is no summary to wait for.
        /// </summary>
        [TestCase(Change.Add)]
        [TestCase(Change.Update)]
        [TestCase(Change.SetEnabled)]
        [TestCase(Change.Delete)]
        public async Task Changes_ReportADatabaseThatWillNotTakeThemAsNotSaved(Change change)
        {
            var id = await AddHostAsync("alpha", "alpha.example");

            // One second of waiting on the lock rather than thirty.
            var impatient = Options($"Data Source={_databasePath};Default Timeout=1");
            var store = Store(impatient);

            // Out of the pool, so that closing it lets go of the lock rather than keeping it.
            await using (var holder = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
            {
                await holder.OpenAsync();
                await ExecuteAsync(holder, "BEGIN IMMEDIATE;");

                var failure = await Assert.ThrowsAsync<IOException>(() => MakeWith(store, change, id).WaitAsync(Patience));

                Assert.Multiple(() =>
                {
                    Assert.That(failure!.Message, Does.StartWith("The change was not saved: "));
                    Assert.That(failure.Message, Does.Contain("locked"));
                    Assert.That(failure.InnerException, Is.InstanceOf<DbUpdateException>().Or.InstanceOf<SqliteException>());
                });

                await ExecuteAsync(holder, "ROLLBACK;");
            }

            var host = (await HostsAsync()).Single();
            Assert.Multiple(() =>
            {
                Assert.That(host.Name, Is.EqualTo("alpha"));
                Assert.That(host.IsEnabled, Is.True);
            });
        }

        /// <summary>
        /// A long history goes a batch at a time, each its own statement and transaction, so that
        /// a ping round coming due waits for one batch rather than the whole history; the other
        /// host's attempts stay. Counted through the statements the store sends.
        /// </summary>
        [Test]
        public async Task DeleteAsync_TakesALongHistoryABatchAtATime()
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            var kept = await AddHostAsync("bravo", "bravo.example", roundtripMs: 15);
            await AddAttemptsAsync(id, HostStore.DeleteBatchSize * 2 + 7);
            var counting = new StatementCounter("DELETE FROM PingAttempts");
            var store = Store(new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={_databasePath}")
                .AddInterceptors(counting)
                .Options);

            var summary = await MakeAsync(() => store.DeleteAsync(id));

            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(counting.Count, Is.EqualTo(3), "two full batches and the seven left");
                Assert.That(Names(summary), Is.EqualTo(new[] { "bravo" }));
                Assert.That(await db.PingAttempts.Select(a => a.HostId).ToListAsync(), Is.EqualTo(new[] { kept }));
            });
        }

        /// <summary>
        /// A delete that fails after its first write says it stopped partway rather than that
        /// nothing was saved, and leaves the host paused with the history it had not yet reached:
        /// paused, so that it is not pinged with half its history, and there, so that deleting it
        /// again finishes the job. A trigger refuses the last batch, standing in for a full disk.
        /// </summary>
        [Test]
        public async Task DeleteAsync_ThatStopsPartway_LeavesTheHostPausedWithTheRest()
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            await AddAttemptsAsync(id, HostStore.DeleteBatchSize + 7);
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER refuse_last BEFORE DELETE ON PingAttempts "
                    + "WHEN OLD.Id = (SELECT MAX(Id) FROM PingAttempts) BEGIN SELECT RAISE(ABORT, 'disk full'); END;");
            }

            var failure = await Assert.ThrowsAsync<IOException>(() => _store.DeleteAsync(id).WaitAsync(Patience));

            var host = (await HostsAsync()).Single();
            await using var check = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(failure!.Message, Is.EqualTo(
                    "The delete stopped partway: SQLite Error 19: 'disk full'. The host is paused, with what was left "
                    + "of its history; delete it again to finish."));
                Assert.That(host.IsEnabled, Is.False);
                Assert.That(await check.PingAttempts.CountAsync(), Is.EqualTo(7), "the first batch went, the last did not");
            });
        }

        /// <summary>
        /// Only the address's own index is the address being taken. Any other constraint that
        /// refuses a host — here a trigger, standing in for whatever a later schema adds — is the
        /// change not being saved, with the database's reason, which is the misreport the page
        /// used to make of every failure. Added through SaveChanges and edited through
        /// ExecuteUpdate, so both ways a refusal reaches the store are covered.
        /// </summary>
        [TestCase(Change.Add)]
        [TestCase(Change.Update)]
        public async Task Changes_ReportAConstraintOtherThanTheAddressAsNotSaved(Change change)
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER refuse_insert BEFORE INSERT ON Hosts BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;");
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER refuse_update BEFORE UPDATE ON Hosts BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;");
            }

            var log = new TestLogger<HostStore>();
            var store = new HostStore(new TestDb.Factory(_options), _summaries, log, new StubClock(Now));

            var failure = await Assert.ThrowsAsync<IOException>(() => MakeWith(store, change, id).WaitAsync(Patience));

            Assert.That(failure!.Message, Is.EqualTo("The change was not saved: SQLite Error 19: 'refused by the test'."));
            Assert.That(
                log.Entries.Select(e => (e.Level, e.Exception)),
                Is.EqualTo(new[] { (LogLevel.Warning, (Exception?)failure.InnerException) }),
                "logged, with the database's own exception, since the page only shows the message");
            Assert.That((await HostsAsync()).Select(h => (h.Name, h.Address)), Is.EqualTo(new[] { ("alpha", "alpha.example") }));
        }

        /// <summary>
        /// Pausing or deleting a host another browser has just deleted is not an error: there is
        /// nothing left to change, and the summary handed back simply no longer lists it. The page
        /// does not catch anything around the switch, so a throw here would end its circuit.
        /// </summary>
        [TestCase(Change.SetEnabled)]
        [TestCase(Change.Delete)]
        public async Task Changes_ToAHostAlreadyGone_HandBackTheSummaryWithoutIt(Change change)
        {
            var id = await AddHostAsync("alpha", "alpha.example", roundtripMs: 12);
            var kept = await AddHostAsync("bravo", "bravo.example", roundtripMs: 15);
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync();
            }

            var summary = change == Change.SetEnabled
                ? await MakeAsync(() => _store.SetEnabledAsync(id, isEnabled: false))
                : await MakeAsync(() => _store.DeleteAsync(id));

            await using var check = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(Names(summary), Is.EqualTo(new[] { "bravo" }));
                Assert.That((await HostsAsync()).Single().IsEnabled, Is.True, "bravo is left running");
                Assert.That(await check.PingAttempts.Select(a => a.HostId).ToListAsync(), Is.EqualTo(new[] { kept }));
            });
        }

        /// <summary>
        /// While a restore has the summary suspended, a change is still made, and what comes back
        /// is the summary in hand: it does not list the change, which reaches the pages with the
        /// first read after the restore. That is the one case where a saved change's summary does
        /// not hold it, and the store's documentation says so.
        /// </summary>
        [Test]
        public async Task AddAsync_WhileTheSummaryIsSuspended_SavesAndHandsBackTheSummaryInHand()
        {
            var inHand = _summaries.Current;
            await _summaries.SuspendAsync();

            var change = await MakeAsync(() => _store.AddAsync("alpha", "alpha.example", isEnabled: true));

            Assert.Multiple(async () =>
            {
                Assert.That(change.Outcome, Is.EqualTo(HostChangeOutcome.Saved));
                Assert.That(change.Summary, Is.SameAs(inHand));
                Assert.That((await HostsAsync()).Select(h => h.Name), Is.EqualTo(new[] { "alpha" }));
            });
        }

        /// <summary>
        /// A delete does not wait for a ping round, which holds the maintenance gate from reading
        /// the hosts until it has saved what it found: with a host down, that is the whole ping
        /// timeout. A delete that waited it out did so with its dialog on screen and nothing to say
        /// why, and was dropped if the page went away meanwhile. The round, which read the host
        /// before it was deleted, still records the host that is left.
        /// </summary>
        /// <remarks>
        /// The store and the round come from one container, as in the application, so that a store
        /// which took turns with the rounds again would be handed the very gate this round holds.
        /// </remarks>
        [Test]
        public async Task DeleteAsync_DoesNotWaitForAPingRoundUnderWay()
        {
            var deletedId = await AddHostAsync("alpha", "alpha.example");
            var keptId = await AddHostAsync("bravo", "bravo.example");
            var pinger = new HeldPingSender();
            await using var services = new ServiceCollection()
                .AddSingleton<IDbContextFactory<HostPingerDbContext>>(new TestDb.Factory(_options))
                .AddSingleton(_settings)
                .AddSingleton(_summaries)
                .AddSingleton<MaintenanceGate>()
                .AddSingleton<DatabasePruner>()
                .AddSingleton<IPingSender>(pinger)
                .AddSingleton<TimeProvider>(new StubClock(Now))
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .AddSingleton<PingMonitorService>()
                .AddSingleton<HostStore>()
                .BuildServiceProvider();

            var round = services.GetRequiredService<PingMonitorService>().RunRoundAsync();
            await pinger.Asked.WaitAsync(Patience);

            var summary = await MakeAsync(() => services.GetRequiredService<HostStore>().DeleteAsync(deletedId));

            Assert.Multiple(() =>
            {
                Assert.That(round.IsCompleted, Is.False, "the round is still out pinging");
                Assert.That(Names(summary), Is.EqualTo(new[] { "bravo" }));
            });

            pinger.Answer();
            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(await round.WaitAsync(Patience), Is.EqualTo(1), "the round records the host that is left");
                Assert.That(await db.PingAttempts.Select(a => a.HostId).ToListAsync(), Is.EqualTo(new[] { keptId }));
            });
        }

        /// <summary>
        /// Pausing and deleting, like adding, end their wait for the summary when cancelled, and the
        /// change stays made.
        /// </summary>
        [TestCase(Change.SetEnabled)]
        [TestCase(Change.Delete)]
        public async Task Changes_CancelledWhileWaitingForTheSummary_StayMade(Change change)
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            using var pageGoesAway = new CancellationTokenSource();
            var making = change == Change.SetEnabled
                ? _store.SetEnabledAsync(id, isEnabled: false, pageGoesAway.Token)
                : _store.DeleteAsync(id, pageGoesAway.Token);

            await pageGoesAway.CancelAsync();

            Assert.That(async () => await making.WaitAsync(Patience), Throws.InstanceOf<OperationCanceledException>());
            var hosts = await HostsAsync();
            Assert.That(change == Change.SetEnabled ? hosts.Single().IsEnabled : hosts.Count != 0, Is.False);
        }

        /// <summary>
        /// Cancelling ends the wait for the summary, which is all the page leaving takes with it: the
        /// change was made before the wait began, and it stays made.
        /// </summary>
        [Test]
        public async Task AddAsync_CancelledWhileWaitingForTheSummary_LeavesTheHostAdded()
        {
            using var pageGoesAway = new CancellationTokenSource();
            var adding = _store.AddAsync("alpha", "alpha.example", isEnabled: true, pageGoesAway.Token);

            await pageGoesAway.CancelAsync();

            Assert.That(async () => await adding.WaitAsync(Patience), Throws.InstanceOf<OperationCanceledException>());
            Assert.That((await HostsAsync()).Select(h => h.Name), Is.EqualTo(new[] { "alpha" }));
        }

        private HostStore Store(DbContextOptions<HostPingerDbContext> options) =>
            new(new TestDb.Factory(options), _summaries, NullLogger<HostStore>.Instance, new StubClock(Now));

        /// <summary>One of each kind of change to the host <paramref name="id"/>, or a new host for an addition.</summary>
        private static Task MakeWith(HostStore store, Change change, int id) => change switch
        {
            Change.Add => store.AddAsync("bravo", "bravo.example", isEnabled: true),
            Change.Update => store.UpdateAsync(id, "renamed", "renamed.example", isEnabled: true),
            Change.SetEnabled => store.SetEnabledAsync(id, isEnabled: false),
            _ => store.DeleteAsync(id),
        };

        private static DbContextOptions<HostPingerDbContext> Options(string connectionString) =>
            new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite(connectionString).Options;

        private static async Task ExecuteAsync(SqliteConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static IEnumerable<string> Names(HostSummarySnapshot summary) => summary.Rows.Select(r => r.Host.Name);

        /// <summary>
        /// Makes a change and takes the read it is waiting for. It is still waiting once the change
        /// has been made — a store handing back the summary in hand would be done already — and is
        /// done once the read has been taken. A delete is made off this thread, so the read is
        /// taken only once the change has asked for it.
        /// </summary>
        private async Task<T> MakeAsync<T>(Func<Task<T>> change)
        {
            var making = change();
            Assert.That(making.IsCompleted, Is.False, "the change waits for a read of its own");
            var deadline = DateTime.UtcNow + Patience;
            while (!_summaries.IsReadRequested && !making.IsCompleted)
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the change asks for a read");
                await Task.Delay(10);
            }

            await _summaries.PollAsync();
            return await making.WaitAsync(Patience);
        }

        /// <summary>A host written on a connection of its own, as a round or another browser would, with the summary caught up.</summary>
        private async Task<int> AddHostAsync(string name, string address, int? roundtripMs = null)
        {
            await using var db = new HostPingerDbContext(_options);
            var host = new MonitoredHost { Name = name, Address = address, CreatedUtc = Now };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            if (roundtripMs is int ms)
            {
                db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = Now, RoundtripMs = ms });
                await db.SaveChangesAsync();
            }

            await _summaries.PollAsync();
            return host.Id;
        }

        /// <summary>A run of attempts for one host, a second apart, oldest first.</summary>
        private async Task AddAttemptsAsync(int hostId, int count)
        {
            await using var db = new HostPingerDbContext(_options);
            db.PingAttempts.AddRange(Enumerable.Range(0, count).Select(i => new PingAttempt
            {
                HostId = hostId,
                TimestampUtc = Now.AddSeconds(-count + i),
                RoundtripMs = null,
            }));
            await db.SaveChangesAsync();
        }

        /// <summary>Counts the statements a context sends that begin a given way.</summary>
        private sealed class StatementCounter(string prefix) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
        {
            private int _count;

            public int Count => _count;

            public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                if (command.CommandText.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _count);
                }

                return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }
        }

        private async Task<List<MonitoredHost>> HostsAsync()
        {
            await using var db = new HostPingerDbContext(_options);
            return await db.Hosts.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
        }

        private sealed class StubClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        /// <summary>
        /// Every host answers, but only once the test says so: until then the round that asked is
        /// out pinging, holding the maintenance gate, as a round waiting on a host that is down is.
        /// </summary>
        private sealed class HeldPingSender : IPingSender
        {
            private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Done once a round has asked for a ping, which it does after reading the hosts.</summary>
            public Task Asked => _asked.Task;

            public void Answer() => _answer.TrySetResult();

            public async Task<PingResult> SendPingAsync(
                string address,
                int timeoutMilliseconds,
                int resolveTimeoutMilliseconds,
                CancellationToken cancellationToken = default)
            {
                _asked.TrySetResult();
                await _answer.Task.WaitAsync(cancellationToken);
                return PingResult.Answered(5);
            }
        }
    }
}
