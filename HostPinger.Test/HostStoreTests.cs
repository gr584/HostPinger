using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        private HostSummaryCache _summaries = null!;
        private MaintenanceGate _gate = null!;
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

            var settings = new UserSettingsStore(
                new TestDb.Factory(_options),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions { RetryAttempts = 3 }),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
            _summaries = new HostSummaryCache(new TestDb.Factory(_options), settings, NullLogger<HostSummaryCache>.Instance);
            await _summaries.LoadAsync();
            _gate = new MaintenanceGate();
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

                var failure = Assert.ThrowsAsync<IOException>(() => MakeWith(store, change, id).WaitAsync(Patience));

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

            var log = new ListLogger();
            var store = new HostStore(new TestDb.Factory(_options), _summaries, _gate, log, new StubClock(Now));

            var failure = Assert.ThrowsAsync<IOException>(() => MakeWith(store, change, id).WaitAsync(Patience));

            Assert.That(failure!.Message, Is.EqualTo("The change was not saved: SQLite Error 19: 'refused by the test'."));
            Assert.That(
                log.Entries,
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
        /// A delete waits for a ping round, backup or restore holding the maintenance gate. A round
        /// reads the hosts and saves an attempt for each at the end, so a host deleted in between
        /// would have the foreign key refuse the round's whole save.
        /// </summary>
        [Test]
        public async Task DeleteAsync_WaitsForTheMaintenanceGate()
        {
            var id = await AddHostAsync("alpha", "alpha.example");
            var round = await _gate.AcquireAsync();

            var deleting = _store.DeleteAsync(id);
            Assert.That(deleting.IsCompleted, Is.False, "the round still holds the gate");
            Assert.That((await HostsAsync()).Select(h => h.Name), Is.EqualTo(new[] { "alpha" }), "nothing deleted yet");

            round.Dispose();
            await WaitUntilAsync(async () => (await HostsAsync()).Count == 0);
            await _summaries.PollAsync();

            Assert.That(Names(await deleting.WaitAsync(Patience)), Is.Empty);
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
            new(new TestDb.Factory(options), _summaries, _gate, NullLogger<HostStore>.Instance, new StubClock(Now));

        /// <summary>One of each kind of change to the host <paramref name="id"/>, or a new host for an addition.</summary>
        private static Task MakeWith(HostStore store, Change change, int id) => change switch
        {
            Change.Add => store.AddAsync("bravo", "bravo.example", isEnabled: true),
            Change.Update => store.UpdateAsync(id, "renamed", "renamed.example", isEnabled: true),
            Change.SetEnabled => store.SetEnabledAsync(id, isEnabled: false),
            _ => store.DeleteAsync(id),
        };

        private static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!await condition())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "gave up waiting");
                await Task.Delay(20);
            }
        }

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
        /// done once the read has been taken.
        /// </summary>
        private async Task<T> MakeAsync<T>(Func<Task<T>> change)
        {
            var making = change();
            Assert.That(making.IsCompleted, Is.False, "the change waits for a read of its own");
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

        private async Task<List<MonitoredHost>> HostsAsync()
        {
            await using var db = new HostPingerDbContext(_options);
            return await db.Hosts.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
        }

        /// <summary>What the store logs, as level and exception.</summary>
        private sealed class ListLogger : ILogger<HostStore>
        {
            public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Entries.Add((logLevel, exception));
        }

        private sealed class StubClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
