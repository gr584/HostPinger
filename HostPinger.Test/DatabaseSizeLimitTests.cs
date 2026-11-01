using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HostPinger.Test
{
    /// <summary>
    /// The size limit held to what the disk can safely take: half of the room the database has,
    /// less the margin, which saving refuses to go past; and a limit of zero, which used to turn
    /// pruning off, given a real one at startup without deleting anything already there. The free
    /// space is stood in for, since a test cannot fill a disk.
    /// </summary>
    public class DatabaseSizeLimitTests
    {
        private const long Mb = 1024 * 1024;

        private string _directory = null!;
        private string _dbPath = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public async Task SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-limit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _dbPath = Path.Combine(_directory, "hostpinger.db");
            _options = new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
            await using var db = new HostPingerDbContext(_options);
            await HostPingerDatabase.EnableIncrementalVacuumAsync(db);
            await db.Database.MigrateAsync();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        [TestCase(2048 + 64, 1024)]
        [TestCase(1000 + 64 + 1, 500)]
        [TestCase(64, 1)]
        [TestCase(0, 1)]
        public void SafeLimit_IsHalfTheRoomLessTheMarginAndNeverUnder1Mb(long roomMb, int expectedMb)
        {
            Assert.That(DatabaseSizeLimit.SafeLimitMbFor(roomMb * Mb), Is.EqualTo(expectedMb));
        }

        /// <summary>
        /// The database's own files are room it has, the -wal and -shm SQLite keeps beside it in
        /// WAL mode included: deleting them would free it. Anything else in the directory is not.
        /// </summary>
        [Test]
        public void Footprint_IsTheFileAndWhatSqliteKeepsBesideIt()
        {
            var path = Path.Combine(_directory, "measured.db");
            File.WriteAllBytes(path, new byte[300_000]);
            File.WriteAllBytes(path + "-wal", new byte[20_000]);
            File.WriteAllBytes(path + "-shm", new byte[1_000]);
            File.WriteAllBytes(path + ".pre-restore", new byte[50_000]);

            Assert.That(DatabaseSizeLimit.FootprintBytes(path), Is.EqualTo(321_000));
        }

        /// <summary>
        /// The disk counts as nearly full under the margin and not at it, and the room measured
        /// carries the safe limit that the database's files and the same free space make: here a
        /// 4 MB file, with 64 MB free coming to (4 + 64 − 64) / 2 = 2 MB.
        /// </summary>
        [TestCase(0, true, 1)]
        [TestCase(DatabaseBackup.FreeSpaceMarginBytes - 1, true, 1)]
        [TestCase(DatabaseBackup.FreeSpaceMarginBytes, false, 2)]
        [TestCase(DatabaseBackup.FreeSpaceMarginBytes + (10 * Mb), false, 7)]
        public void MeasureRoom_SaysTheDiskIsNearlyFullUnderTheMargin(long freeBytes, bool nearlyFull, int safeLimitMb)
        {
            var path = Path.Combine(_directory, "measured.db");
            File.WriteAllBytes(path, new byte[4 * Mb]);
            var sizeLimit = new DatabaseSizeLimit(new PingerPaths(path), Store(new PingerOptions()), new TestLogger<DatabaseSizeLimit>())
            {
                MeasureFreeBytes = () => freeBytes,
            };

            var room = sizeLimit.MeasureRoom();

            Assert.Multiple(() =>
            {
                Assert.That(room.FreeBytes, Is.EqualTo(freeBytes));
                Assert.That(room.IsDiskNearlyFull, Is.EqualTo(nearlyFull));
                Assert.That(room.SafeLimitMb, Is.EqualTo(safeLimitMb));
            });
        }

        [Test]
        public async Task Save_TakesALimitUpToTheSafeOne()
        {
            var (store, sizeLimit, _) = Create(freeBytes: 2048 * Mb);
            var safe = sizeLimit.SafeLimitMb();

            var refusal = await sizeLimit.SaveAsync(safe);

            Assert.Multiple(() =>
            {
                Assert.That(safe, Is.EqualTo(DatabaseSizeLimit.SafeLimitMbFor(DatabaseSizeLimit.FootprintBytes(_dbPath) + (2048 * Mb))));
                Assert.That(refusal, Is.Null);
                Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(safe));
            });
        }

        [Test]
        public async Task Save_RefusesMoreThanTheDiskCanSafelyHold()
        {
            var (store, sizeLimit, _) = Create(freeBytes: 2048 * Mb);
            var safe = sizeLimit.SafeLimitMb();

            var refusal = await sizeLimit.SaveAsync(safe + 1);

            Assert.Multiple(() =>
            {
                Assert.That(refusal, Does.StartWith($"This disk can safely hold {safe:N0} MB at most"));
                Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(new PingerOptions().MaxDatabaseSizeMb),
                    "nothing was saved");
            });
        }

        /// <summary>Zero used to turn pruning off, and is refused now, as is anything less.</summary>
        [TestCase(0)]
        [TestCase(-5)]
        public async Task Save_RefusesALimitUnder1Mb(int limitMb)
        {
            var (store, sizeLimit, _) = Create(freeBytes: 2048 * Mb);

            var refusal = await sizeLimit.SaveAsync(limitMb);

            Assert.Multiple(() =>
            {
                Assert.That(refusal, Is.EqualTo("Enter a size of at least 1 MB."));
                Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(new PingerOptions().MaxDatabaseSizeMb));
            });
        }

        /// <summary>
        /// A zero stored by an earlier version, which turned pruning off, becomes the safe limit as
        /// the service starts, stored so that it stays, with a warning saying what it now is.
        /// </summary>
        [Test]
        public async Task Settle_GivesAStoredZeroTheSafeLimit()
        {
            var (store, sizeLimit, log) = Create(freeBytes: 2048 * Mb);
            await store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 0 });

            await sizeLimit.SettleAsync();

            var restarted = Store(new PingerOptions());
            await restarted.LoadAsync();
            Assert.Multiple(() =>
            {
                Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(sizeLimit.SafeLimitMb()));
                Assert.That(restarted.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(sizeLimit.SafeLimitMb()), "stored");
                Assert.That(log.Entries.Select(e => e.Level), Is.EqualTo(new[] { LogLevel.Warning }));
                Assert.That(log.Entries.Single().Message, Does.StartWith("The database size limit was 0 MB"));
            });
        }

        /// <summary>A zero from appsettings.json or the environment is no more a limit than a stored one.</summary>
        [Test]
        public async Task Settle_GivesAConfiguredZeroARealLimitToo()
        {
            var (store, sizeLimit, _) = Create(freeBytes: 2048 * Mb, configured: new PingerOptions { MaxDatabaseSizeMb = 0 });

            await sizeLimit.SettleAsync();

            Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(sizeLimit.SafeLimitMb()));
        }

        /// <summary>
        /// A database already larger than the safe limit, as one grown under a zero can be, gets its
        /// own size as the limit instead, so the start that replaces the zero prunes none of the
        /// history that is there.
        /// </summary>
        [Test]
        public async Task Settle_KeepsADatabaseLargerThanTheSafeLimitWhole()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                var host = new MonitoredHost { Name = "kept", Address = "kept.example" };
                db.Hosts.Add(host);
                await db.SaveChangesAsync();
                var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                db.PingAttempts.AddRange(Enumerable.Range(0, 40_000).Select(i => new PingAttempt
                {
                    HostId = host.Id,
                    TimestampUtc = start.AddSeconds(i),
                    RoundtripMs = i % 100,
                }));
                await db.SaveChangesAsync();
            }

            var (store, sizeLimit, _) = Create(freeBytes: 0, configured: new PingerOptions { MaxDatabaseSizeMb = 0 });
            Assert.That(DatabaseSizeLimit.FootprintBytes(_dbPath), Is.GreaterThan(2 * Mb), "more than the 1 MB the safe limit comes to");

            await sizeLimit.SettleAsync();

            await using var check = new HostPingerDbContext(_options);
            var pruned = await new DatabasePruner().EnforceSizeLimitAsync(check, store.CurrentPinger.MaxDatabaseSizeBytes, TimeSpan.MaxValue);
            Assert.Multiple(async () =>
            {
                Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.GreaterThan(1));
                Assert.That(pruned, Is.Zero, "nothing is over the limit it was given");
                Assert.That(await check.PingAttempts.CountAsync(), Is.EqualTo(40_000));
            });
        }

        /// <summary>
        /// A positive limit is left as it is, even one above what is safe now: something else filling
        /// the disk is the page's to point out, not a reason to prune.
        /// </summary>
        [TestCase(300)]
        [TestCase(500_000)]
        public async Task Settle_LeavesAPositiveLimitAlone(int limitMb)
        {
            var (store, sizeLimit, log) = Create(freeBytes: 2048 * Mb);
            await store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = limitMb });

            await sizeLimit.SettleAsync();

            Assert.Multiple(() =>
            {
                Assert.That(store.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(limitMb));
                Assert.That(log.Entries, Is.Empty);
            });
        }

        private (UserSettingsStore Store, DatabaseSizeLimit SizeLimit, TestLogger<DatabaseSizeLimit> Log) Create(
            long freeBytes,
            PingerOptions? configured = null)
        {
            var store = Store(configured ?? new PingerOptions());
            var log = new TestLogger<DatabaseSizeLimit>();
            return (store, new DatabaseSizeLimit(new PingerPaths(_dbPath), store, log) { MeasureFreeBytes = () => freeBytes }, log);
        }

        private UserSettingsStore Store(PingerOptions configured) =>
            new(new TestDb.Factory(_options),
                new TestOptionsMonitor<PingerOptions>(configured),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
    }
}
