using System.Runtime.Versioning;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    /// <summary>
    /// Exercises <see cref="DatabaseBackup"/> against real files, because what it does — copy,
    /// validate, swap — is exactly the part an in-memory database cannot stand in for.
    /// </summary>
    public class DatabaseBackupTests
    {
        /// <summary>The last migration 1.12.0 shipped, with both index rebuilds still to come.</summary>
        private const string LastIn1_12 = "20260829041132_AddUserSettings";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        /// <summary>The password of the backups the uploads restore.</summary>
        private const string BackupPassword = "the backup's password";

        /// <summary>Its hash, made once for the run: each one is a tenth of a second of hashing.</summary>
        private static readonly string BackupHash = PasswordHash.Hash(BackupPassword);

        private string _directory = null!;
        private string _dbPath = null!;

        /// <summary>
        /// Where <see cref="KeepWhatWasRetiredAsync"/> copies the database a restore replaced, which
        /// the restore deletes once the backup is in force.
        /// </summary>
        private string _retired = null!;

        private DatabaseFileGate _fileGate = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;
        private UserSettingsStore _store = null!;
        private HostSummaryCache _summaries = null!;
        private DatabaseBackup _backup = null!;

        [SetUp]
        public async Task SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _dbPath = Path.Combine(_directory, "hostpinger.db");
            _retired = Path.Combine(_directory, "retired.db");

            // Through a gate of its own, as the application's factory goes through one, so that a
            // restore's own connections have to be let through it.
            _fileGate = new DatabaseFileGate();
            _options = OptionsFor(_dbPath, _fileGate);

            // Migrated rather than EnsureCreated, because validation and restore both read the
            // migration history — the shape a real database has.
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.MigrateAsync();
            }

            _store = new UserSettingsStore(
                new TestDb.Factory(_options),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions()),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
            _summaries = new HostSummaryCache(new TestDb.Factory(_options), _store, NullLogger<HostSummaryCache>.Instance);
            _backup = new DatabaseBackup(
                new PingerPaths(_dbPath),
                new TestDb.Factory(_options),
                _store,
                new MaintenanceGate(),
                _fileGate,
                _summaries,
                NullLogger<DatabaseBackup>.Instance)
            {
                BeforeDeletingRetired = KeepWhatWasRetiredAsync,
            };
        }

        [TearDown]
        public void TearDown()
        {
            _summaries.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task CreateSnapshot_ProducesStandaloneEqualDatabase()
        {
            await SeedHostWithAttemptsAsync(_options, "seeded.example", attempts: 250);

            var snapshotPath = await _backup.CreateSnapshotAsync();

            Assert.That(Path.GetDirectoryName(snapshotPath), Is.EqualTo(_directory),
                "the snapshot must land beside the database, the only directory the service can write");

            await using (var copy = new HostPingerDbContext(OptionsFor(snapshotPath)))
            {
                Assert.Multiple(async () =>
                {
                    Assert.That(await copy.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("seeded.example"));
                    Assert.That(await copy.PingAttempts.CountAsync(), Is.EqualTo(250));
                    Assert.That(await IntegrityCheckAsync(snapshotPath), Is.EqualTo("ok"));
                });
            }

            await using var source = new HostPingerDbContext(_options);
            Assert.That(await source.PingAttempts.CountAsync(), Is.EqualTo(250),
                "taking a snapshot must leave the live database untouched");
        }

        [Test]
        public async Task ValidateBackup_RejectsGarbageFile()
        {
            var garbagePath = Path.Combine(_directory, "garbage.bin");
            await File.WriteAllBytesAsync(garbagePath, [1, 2, 3, 4, 5, 6, 7, 8]);

            Assert.That(await _backup.ValidateBackupAsync(garbagePath), Is.EqualTo("This file is not an SQLite database."));
        }

        [Test]
        public async Task ValidateBackup_RejectsDatabaseWithoutMigrationHistory()
        {
            // EnsureCreated builds the schema without recording migrations, which is also the
            // shape of any foreign SQLite file: valid database, not a HostPinger one.
            var foreignPath = Path.Combine(_directory, "foreign.db");
            await using (var db = new HostPingerDbContext(OptionsFor(foreignPath)))
            {
                await db.Database.EnsureCreatedAsync();
            }

            Assert.That(await _backup.ValidateBackupAsync(foreignPath), Is.EqualTo("No HostPinger schema was found in this file."));
        }

        [Test]
        public async Task ValidateBackup_RejectsNewerMigrations()
        {
            var newerPath = Path.Combine(_directory, "newer.db");
            await using (var db = new HostPingerDbContext(OptionsFor(newerPath)))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('99991231000000_FromTheFuture', '99.0.0');");
            }

            Assert.That(await _backup.ValidateBackupAsync(newerPath), Is.EqualTo("This backup was made by a newer version of HostPinger."));
        }

        [Test]
        public async Task ValidateBackup_AcceptsOlderDatabase()
        {
            // A backup from an older build has the earlier migrations and not the later ones;
            // deleting the newest history row reproduces that shape without keeping old builds
            // around. Restore migrates such a file up, so validation must let it through.
            var olderPath = Path.Combine(_directory, "older.db");
            await using (var db = new HostPingerDbContext(OptionsFor(olderPath)))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM __EFMigrationsHistory WHERE MigrationId = (SELECT MAX(MigrationId) FROM __EFMigrationsHistory);");
            }

            Assert.That(await _backup.ValidateBackupAsync(olderPath), Is.Null);
        }

        [Test]
        public async Task ValidateBackup_AcceptsItsOwnSnapshot()
        {
            var snapshotPath = await _backup.CreateSnapshotAsync();

            Assert.That(await _backup.ValidateBackupAsync(snapshotPath), Is.Null);
        }

        /// <summary>
        /// The migrations define no triggers and no views, so a file with either was shaped by
        /// something else. The trigger here writes the backup's own password back whenever the
        /// settings change, which is what a restored one of these did: neither the Password page
        /// nor --remove-password could get rid of the password it planted.
        /// </summary>
        [TestCase("CREATE TRIGGER KeepPassword AFTER UPDATE ON UserSettings BEGIN UPDATE UserSettings SET Value = 'planted' WHERE Key = 'Security:PasswordHash'; END;")]
        [TestCase("CREATE VIEW Everything AS SELECT * FROM Hosts;")]
        public async Task ValidateBackup_RefusesAFileDefiningATriggerOrAView(string definition)
        {
            var craftedPath = Path.Combine(_directory, "crafted.db");
            await using (var db = new HostPingerDbContext(OptionsFor(craftedPath)))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(definition);
            }

            Assert.That(await _backup.ValidateBackupAsync(craftedPath),
                Is.EqualTo("This file defines triggers or views, which a HostPinger database never has."));
        }

        /// <summary>
        /// A restore swaps the backup in and reloads the settings from it, and then deletes the
        /// database it replaced, which waits beside the restored one until then.
        /// </summary>
        [Test]
        public async Task Restore_SwapsTheFileInReloadsSettingsAndDeletesWhatItReplaced()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");

            var backupPath = Path.Combine(_directory, "uploaded.db");
            var backupOptions = OptionsFor(backupPath);
            await using (var db = new HostPingerDbContext(backupOptions))
            {
                await db.Database.MigrateAsync();
            }

            await SeedHostWithAttemptsAsync(backupOptions, "backup.example", attempts: 3);
            await StagePasswordAsync(backupOptions, "backup-hash");

            await _store.LoadAsync();
            Assert.That(_store.PasswordHash, Is.EqualTo("current-hash"), "the state before the restore");

            await _backup.RestoreAsync(backupPath);

            await using var live = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(await live.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("backup.example"),
                    "the live path must now answer with the backup's data");
                Assert.That(await live.PingAttempts.CountAsync(), Is.EqualTo(3));
                Assert.That(_store.PasswordHash, Is.EqualTo("backup-hash"),
                    "the settings snapshot must already reflect the restored file");
                Assert.That(await AddressesAsync(_retired), Is.EqualTo(new[] { "current.example" }),
                    "the database replaced waited beside it until then");
                Assert.That(File.Exists(_backup.PreRestorePath), Is.False, "and is deleted once the backup is in force");
                Assert.That(File.Exists(backupPath), Is.False, "the swap consumes the uploaded file");
            });
        }

        /// <summary>
        /// A database replaced that will not delete is left where it is, with a warning in the log
        /// naming it, rather than failing a restore that has happened: the page would report a
        /// failure with the backup's settings and password already in force. Made to fail here by
        /// taking away the right to change the directory, which is how Linux refuses a delete.
        /// </summary>
        [Test]
        [Platform(Exclude = "Win", Reason = "Windows refuses a delete through a handle held open, not the directory's permissions.")]
        [UnsupportedOSPlatform("windows")]
        public async Task Restore_LeavesWhatItReplacedWithAWarningWhenItWillNotDelete()
        {
            Assume.That(Environment.UserName, Is.Not.EqualTo("root"), "permissions stop nothing root does");
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");
            var log = new TestLogger<DatabaseBackup>();
            var mode = File.GetUnixFileMode(_directory);
            var backup = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, log)
            {
                BeforeDeletingRetired = () =>
                {
                    File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    return Task.CompletedTask;
                },
            };

            try
            {
                await backup.RestoreAsync(backupPath);
            }
            finally
            {
                File.SetUnixFileMode(_directory, mode);
            }

            Assert.Multiple(async () =>
            {
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example" }));
                Assert.That(_store.PasswordHash, Is.EqualTo("backup-hash"));
                Assert.That(await AddressesAsync(backup.PreRestorePath), Is.EqualTo(new[] { "current.example" }),
                    "the database replaced is left where it was");
                Assert.That(log.Entries.Select(e => e.Level), Is.EqualTo(new[] { LogLevel.Warning }));
                Assert.That(log.Entries.Single().Message, Does.Contain(backup.PreRestorePath));
            });
        }

        /// <summary>
        /// The host summary keeps a connection of its own open, which a swap leaves pointing at the
        /// file it retired. A restore has it let go and read again, so the Hosts page shows the
        /// backup's hosts rather than the ones that were restored over.
        /// </summary>
        [Test]
        public async Task Restore_HandsTheHostSummaryTheRestoredHosts()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var backupPath = Path.Combine(_directory, "uploaded.db");
            var backupOptions = OptionsFor(backupPath);
            await using (var db = new HostPingerDbContext(backupOptions))
            {
                await db.Database.MigrateAsync();
            }

            await SeedHostWithAttemptsAsync(backupOptions, "backup.example", attempts: 1);
            await _store.LoadAsync();
            await _summaries.LoadAsync();
            Assert.That(_summaries.Current.Rows.Select(r => r.Host.Address), Is.EqualTo(new[] { "current.example" }),
                "the state before the restore");

            await _backup.RestoreAsync(backupPath);
            await _summaries.PollAsync();

            Assert.That(_summaries.Current.Rows.Select(r => r.Host.Address), Is.EqualTo(new[] { "backup.example" }));
        }

        /// <summary>
        /// A backup comes out of VACUUM INTO in rollback-journal mode, where a long read keeps the
        /// ping rounds from committing. The restore puts it into WAL mode, the mode a new database
        /// starts in, and leaves the file it retires in rollback-journal mode, whole on its own —
        /// whichever mode that file was in: a new database is in WAL mode, and one restored before
        /// this was in the other.
        /// </summary>
        [TestCase("wal")]
        [TestCase("delete")]
        public async Task Restore_PutsTheRestoredDatabaseInWalMode(string retiredMode)
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var backupPath = await _backup.CreateSnapshotAsync();
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await ScalarAsync(connection, $"PRAGMA journal_mode = {retiredMode};");
            }

            Assert.Multiple(async () =>
            {
                Assert.That(await JournalModeAsync(_dbPath), Is.EqualTo(retiredMode));
                Assert.That(await JournalModeAsync(backupPath), Is.EqualTo("delete"), "the mode VACUUM INTO writes");
            });

            await _backup.RestoreAsync(backupPath);

            Assert.Multiple(async () =>
            {
                Assert.That(await JournalModeAsync(_dbPath), Is.EqualTo("wal"));
                Assert.That(await JournalModeAsync(_retired), Is.EqualTo("delete"),
                    "the file kept to put back needs nothing beside it");

                // Switching the upload to WAL mode happens on a connection of its own, closed before
                // the swap; nothing of it may be left under the upload's name.
                Assert.That(Directory.GetFiles(_directory, Path.GetFileName(backupPath) + "*"), Is.Empty,
                    "nothing of the upload is left, a -wal or -shm included");
            });
        }

        /// <summary>
        /// A read still running on the file being retired is waited out before the swap. The last
        /// connection to close on a WAL database deletes the -wal and -shm beside it by name, and
        /// after the swap those are the restored file's: had the restore gone ahead, closing this
        /// one would have taken them, and whatever was written to the restored file meanwhile would
        /// be invisible to every connection opened after.
        /// </summary>
        [Test]
        public async Task Restore_WaitsOutAReadStillOpenOnTheRetiredFile()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var backupPath = await _backup.CreateSnapshotAsync();

            // Outside the pool, the way a read still running is out of ClearAllPools' reach.
            var reading = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            await reading.OpenAsync();
            await ScalarAsync(reading, "SELECT COUNT(*) FROM Hosts;");

            // On a thread of its own, because the provider waits out a busy file by sleeping.
            var restore = Task.Run(() => _backup.RestoreAsync(backupPath));
            await Task.WhenAny(restore, Task.Delay(TimeSpan.FromSeconds(2)));
            var restoredWhileReading = restore.IsCompleted;
            await reading.DisposeAsync();
            await restore.WaitAsync(TimeSpan.FromSeconds(30));

            // Written through the pool, where the restore left connections to the restored file
            // open, and read back on a connection opened afterwards.
            await SeedHostWithAttemptsAsync(_options, "after.example", attempts: 1);
            await using var afterwards = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            await afterwards.OpenAsync();

            Assert.Multiple(async () =>
            {
                Assert.That(restoredWhileReading, Is.False, "the swap waits for the read to finish");
                Assert.That(
                    await ScalarAsync(afterwards, "SELECT COUNT(*) FROM Hosts WHERE Address = 'after.example';"),
                    Is.EqualTo(1L));
            });
        }

        /// <summary>
        /// What a run stopped partway through a snapshot or a restore leaves: the temp file, and
        /// the journal, -wal or -shm SQLite was keeping beside it, which used to stay for good. The
        /// database's own files, and anything else that only starts with the same name, stay.
        /// </summary>
        [Test]
        public async Task DeleteLeftoverTempFiles_SweepsOnlyItsOwn()
        {
            string[] leftovers =
            [
                "hostpinger-backup-dead.tmp", "hostpinger-backup-dead.tmp-journal",
                "hostpinger-restore-dead.tmp", "hostpinger-restore-dead.tmp-journal",
                "hostpinger-restore-dead.tmp-wal", "hostpinger-restore-dead.tmp-shm",
            ];
            foreach (var leftover in leftovers)
            {
                await File.WriteAllTextAsync(Path.Combine(_directory, leftover), "left behind by a crash");
            }

            var notOurs = Path.Combine(_directory, "hostpinger-backup-notes.txt");
            await File.WriteAllTextAsync(notOurs, "somebody's own file");
            var databaseFiles = Directory.GetFiles(_directory, "hostpinger.db*");

            DatabaseBackup.DeleteLeftoverTempFiles(new PingerPaths(_dbPath));

            Assert.Multiple(() =>
            {
                Assert.That(leftovers.Where(leftover => File.Exists(Path.Combine(_directory, leftover))), Is.Empty);
                Assert.That(databaseFiles, Does.Contain(_dbPath));
                Assert.That(databaseFiles.Where(File.Exists), Is.EquivalentTo(databaseFiles),
                    "the database and whatever SQLite keeps beside it are not leftovers");
                Assert.That(File.Exists(notOurs), "a name the sweep does not make is not one of its files");
            });
        }

        /// <summary>
        /// Where an upload lands: beside the database, where the hardened services can write, under
        /// a name of its own each time, and one the sweep at startup recognises as its own.
        /// </summary>
        [Test]
        public void CreateRestoreTempPath_IsBesideTheDatabaseFreshEachTimeAndSweptAtStartup()
        {
            var first = _backup.CreateRestoreTempPath();
            var second = _backup.CreateRestoreTempPath();
            File.WriteAllText(first, "an upload cut off by a restart");

            DatabaseBackup.DeleteLeftoverTempFiles(new PingerPaths(_dbPath));

            Assert.Multiple(() =>
            {
                Assert.That(Path.GetDirectoryName(first), Is.EqualTo(_directory));
                Assert.That(first, Is.Not.EqualTo(second));
                Assert.That(File.Exists(first), Is.False, "swept as a leftover");
            });
        }

        /// <summary>What the headroom checks compare against: the free space of the disk the database is on.</summary>
        [Test]
        public void AvailableFreeBytes_IsWhatTheDatabasesDiskHasFree()
        {
            var expected = new DriveInfo(_directory).AvailableFreeSpace;

            Assert.That(_backup.AvailableFreeBytes(), Is.Positive.And.EqualTo(expected).Within(256L * 1024 * 1024));
        }

        /// <summary>
        /// A snapshot that fails partway leaves nothing behind for a retried download to find.
        /// The attempts table is damaged in the middle, so the copy has begun writing the snapshot
        /// by the time it reaches the damage and gives up.
        /// </summary>
        [Test]
        public async Task CreateSnapshot_LeavesNothingBehindWhenItFails()
        {
            await SeedHostWithAttemptsAsync(_options, "damaged.example", attempts: 5000);
            var brokenPath = Path.Combine(_directory, "broken.db");
            File.Copy(await _backup.CreateSnapshotAsync(), brokenPath);
            await using (var connection = new SqliteConnection($"Data Source={brokenPath};Pooling=False"))
            {
                await connection.OpenAsync();
                var pageSize = Convert.ToInt64(await ScalarAsync(connection, "PRAGMA page_size;"));
                var pages = Convert.ToInt64(await ScalarAsync(connection, "PRAGMA page_count;"));
                await connection.CloseAsync();
                await using var file = new FileStream(brokenPath, FileMode.Open, FileAccess.Write);
                file.Position = pages / 2 * pageSize;
                await file.WriteAsync(Enumerable.Repeat((byte)0x7F, (int)pageSize * 4).ToArray());
            }

            foreach (var leftover in Directory.EnumerateFiles(_directory, "hostpinger-backup-*"))
            {
                File.Delete(leftover);
            }

            var backup = new DatabaseBackup(
                new PingerPaths(brokenPath),
                new TestDb.Factory(OptionsFor(brokenPath)),
                _store,
                new MaintenanceGate(),
                _fileGate,
                _summaries,
                NullLogger<DatabaseBackup>.Instance);

            Assert.That(async () => await backup.CreateSnapshotAsync(),
                Throws.InstanceOf<IOException>().With.InnerException.InstanceOf<SqliteException>());
            Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-backup-*"), Is.Empty);
        }

        /// <summary>
        /// A snapshot says how far it has got while it runs, not only once it is done: fractions
        /// that only grow, short of 1 until the 1 at the end. The database is filled until copying
        /// it takes many of the shortened intervals.
        /// </summary>
        [Test]
        public async Task CreateSnapshot_SaysHowFarItHasGot()
        {
            await FillAsync(_dbPath, megabytes: 40);
            var reports = new List<double>();

            await SnapshotsWatchedEvery(TimeSpan.FromMilliseconds(1)).CreateSnapshotAsync(new Recorder<double>(reports.Add));

            Assert.Multiple(() =>
            {
                Assert.That(reports, Is.Ordered);
                Assert.That(reports[^1], Is.EqualTo(1));
                Assert.That(reports[..^1], Has.All.InRange(0, 0.99));
                Assert.That(reports, Has.Some.InRange(0.1, 0.9), "partway through, not only before and after");
            });
        }

        /// <summary>
        /// In WAL mode a backup no longer holds the ping rounds off while it copies. A round coming
        /// due partway through takes the gate and writes, and what it writes lands in the database
        /// and not in the copy, which is of the moment the copy began, and whole.
        /// </summary>
        [Test]
        public async Task CreateSnapshot_LetsTheRoundsRunWhileItCopies()
        {
            await FillAsync(_dbPath, megabytes: 40);
            var gate = new MaintenanceGate();
            var round = (string?)null;
            var backup = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, gate, _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                SnapshotProgressInterval = TimeSpan.FromMilliseconds(1),
            };

            var snapshotPath = await backup.CreateSnapshotAsync(new Recorder<double>(fraction =>
            {
                if (fraction is > 0 and < 1 && round is null)
                {
                    round = RoundPartwayThrough(gate, TimeSpan.FromSeconds(1),
                        () => SeedHostWithAttemptsAsync(_options, "during.example", attempts: 1).GetAwaiter().GetResult());
                }
            }));

            Assert.Multiple(async () =>
            {
                Assert.That(round, Is.EqualTo("ran"));
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "during.example" }));
                Assert.That(await AddressesAsync(snapshotPath), Is.Empty, "the copy is of the moment it began");
                Assert.That(await IntegrityCheckAsync(snapshotPath), Is.EqualTo("ok"));
            });
        }

        /// <summary>
        /// In rollback-journal mode the copy's read would keep a round from committing until the
        /// provider gave up and the round's pings were lost, so there a backup still holds the
        /// rounds off at the gate while it copies, and lets go once it is done.
        /// </summary>
        [Test]
        public async Task CreateSnapshot_HoldsTheRoundsOffInRollbackJournalMode()
        {
            await FillAsync(_dbPath, megabytes: 40);
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await ScalarAsync(connection, "PRAGMA journal_mode = DELETE;");
            }

            var gate = new MaintenanceGate();
            var round = (string?)null;
            var backup = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, gate, _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                SnapshotProgressInterval = TimeSpan.FromMilliseconds(1),
            };

            await backup.CreateSnapshotAsync(new Recorder<double>(fraction =>
            {
                if (fraction is > 0 and < 1 && round is null)
                {
                    round = RoundPartwayThrough(gate, TimeSpan.FromMilliseconds(300), () => { });
                }
            }));

            Assert.That(round, Is.EqualTo("held off"));
            using (await gate.AcquireAsync().WaitAsync(Patience))
            {
            }
        }

        /// <summary>
        /// Cancelling stops the copy where it is, where the token alone would let Microsoft.Data.Sqlite
        /// run it to the end; deletes what had been written; and lets go of the maintenance gate, so
        /// the next snapshot goes ahead.
        /// </summary>
        [Test]
        public async Task CreateSnapshot_CancelledStopsTheCopyAndLeavesNothing()
        {
            await FillAsync(_dbPath, megabytes: 40);
            using var cancel = new CancellationTokenSource();
            var reports = new List<double>();
            var backup = SnapshotsWatchedEvery(TimeSpan.FromMilliseconds(1));

            // Once the copy has begun writing, so there is a part-written file to delete.
            var cancelled = backup.CreateSnapshotAsync(
                new Recorder<double>(fraction =>
                {
                    reports.Add(fraction);
                    if (fraction > 0)
                    {
                        cancel.Cancel();
                    }
                }),
                cancel.Token);

            Assert.That(async () => await cancelled, Throws.InstanceOf<OperationCanceledException>());
            Assert.Multiple(() =>
            {
                Assert.That(reports.Max(), Is.LessThan(0.5), "stopped soon after the cancel, not at the end");
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-backup-*"), Is.Empty);
            });
            Assert.That(File.Exists(await backup.CreateSnapshotAsync().WaitAsync(Patience)), "the gate was let go of");
        }

        /// <summary>
        /// A file that starts like a database and is damaged after its header is refused, whether
        /// the damage is somewhere SQLite only notices when it checks — an index no longer matching
        /// its table — or somewhere it cannot read past at all, the schema itself.
        /// </summary>
        [TestCase("index")]
        [TestCase("schema")]
        public async Task ValidateBackup_RejectsAFileDamagedAfterItsHeader(string damaged)
        {
            await SeedHostWithAttemptsAsync(_options, "damaged.example", attempts: 2000);
            var copy = await _backup.CreateSnapshotAsync();
            long offset;
            int length;
            await using (var connection = new SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                await connection.OpenAsync();
                var pageSize = Convert.ToInt64(await ScalarAsync(connection, "PRAGMA page_size;"));
                if (damaged == "schema")
                {
                    // The rest of the first page, after the file header, is where the schema lives.
                    offset = 100;
                    length = (int)pageSize - 100;
                }
                else
                {
                    // The tail of an index page holds its entries' keys; changing them leaves rows
                    // the index no longer finds.
                    var root = Convert.ToInt64(await ScalarAsync(
                        connection, "SELECT rootpage FROM sqlite_master WHERE name = 'IX_PingAttempts_HostId_TimestampUtc';"));
                    offset = (root - 1) * pageSize + pageSize - 64;
                    length = 48;
                }
            }

            await using (var file = new FileStream(copy, FileMode.Open, FileAccess.Write))
            {
                file.Position = offset;
                await file.WriteAsync(Enumerable.Repeat((byte)0x7F, length).ToArray());
            }

            Assert.That(await _backup.ValidateBackupAsync(copy), Is.EqualTo("The file failed SQLite's integrity check."));
        }

        /// <summary>
        /// Restoring onto an install that has no database yet moves the backup into place, with no
        /// undo copy to keep, since there was nothing to undo.
        /// </summary>
        [Test]
        public async Task Restore_IntoAFreshInstallMovesTheBackupIntoPlace()
        {
            await SeedHostWithAttemptsAsync(_options, "backed-up.example", attempts: 1);
            var backupPath = await _backup.CreateSnapshotAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(_dbPath + suffix);
            }

            await _backup.RestoreAsync(backupPath);

            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(await db.Hosts.Select(h => h.Address).ToListAsync(), Is.EqualTo(new[] { "backed-up.example" }));
                Assert.That(File.Exists(_backup.PreRestorePath), Is.False);
                Assert.That(File.Exists(backupPath), Is.False, "consumed by the move");
            });
        }

        /// <summary>
        /// A connection that holds the database open for the whole of the restore's wait stops the
        /// restore before it replaces anything, with a reason the page can show, rather than a swap
        /// that would let that connection delete the restored file's -wal as it closed.
        /// </summary>
        [Test]
        public async Task Restore_RefusesWhileTheRetiredFileStaysInUse()
        {
            await SeedHostWithAttemptsAsync(_options, "kept.example", attempts: 1);
            var backupPath = await _backup.CreateSnapshotAsync();
            await SeedHostWithAttemptsAsync(_options, "since.example", attempts: 1);
            var backup = new DatabaseBackup(
                new PingerPaths(_dbPath),
                new TestDb.Factory(_options),
                _store,
                new MaintenanceGate(),
                _fileGate,
                _summaries,
                NullLogger<DatabaseBackup>.Instance)
            {
                BusyTimeoutSeconds = 1,
            };

            await using (var holding = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
            {
                await holding.OpenAsync();
                await ScalarAsync(holding, "PRAGMA journal_mode = WAL;");

                var refusal = await Assert.ThrowsAsync<IOException>(() => backup.RestoreAsync(backupPath));
                Assert.That(refusal!.Message, Is.EqualTo("The database stayed in use, so nothing was replaced. Try the restore again."));
            }

            SqliteConnection.ClearAllPools();
            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(await db.Hosts.CountAsync(h => h.Address == "since.example"), Is.EqualTo(1), "the database was not replaced");
                Assert.That(File.Exists(backupPath), Is.True, "the backup was not consumed");
                Assert.That(File.Exists(backup.PreRestorePath), Is.False);
            });
        }

        /// <summary>
        /// A page writing while a restore waits for the file — another browser saving a host, say —
        /// used to open a connection that went back into the pool still holding the file, so the
        /// restore gave up once its wait ran out; and one that came after the file had left WAL mode
        /// wrote into the file being retired. Now the page's connection waits at the gate until the
        /// restored file is in place, and the write lands there. The restore's wait is held open
        /// here by a connection from outside the gate, as another process's would be.
        /// </summary>
        [Test]
        public async Task Restore_HoldsBackAPageWriteUntilTheRestoredFileIsInPlace()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");

            await using var outside = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            await outside.OpenAsync();
            await ScalarAsync(outside, "SELECT COUNT(*) FROM Hosts;");

            var restore = Task.Run(() => _backup.RestoreAsync(backupPath));
            await WaitUntilAsync(() => _fileGate.IsShut, "the restore shuts the gate before it waits for the file");
            var write = Task.Run(() => SeedHostWithAttemptsAsync(_options, "written-meanwhile.example", attempts: 1));

            // Long enough for a write that was not held back to have finished.
            await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(1)));
            var writtenWhileShut = write.IsCompleted;
            await outside.CloseAsync();
            await restore.WaitAsync(Patience);
            await write.WaitAsync(Patience);

            Assert.Multiple(async () =>
            {
                Assert.That(writtenWhileShut, Is.False, "the write waits for the restore");
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example", "written-meanwhile.example" }),
                    "the write lands in the restored file");
                Assert.That(await AddressesAsync(_retired), Is.EqualTo(new[] { "current.example" }),
                    "and nothing of it in the file retired");
                Assert.That(await IntegrityCheckAsync(_dbPath), Is.EqualTo("ok"));
            });
        }

        /// <summary>
        /// In rollback-journal mode an open connection holds no lock between its statements, so
        /// SQLite has nothing to say about it and only the gate keeps a restore from swapping the
        /// file under a page's write. Swapped then, the write's journal sat beside the restored file
        /// under the live name, and the restored file was the next to open it: it rolled the
        /// journal in, with pages of the file it replaced. Here the write is held open on purpose,
        /// over every one of the current file's attempts, until the restore is waiting for it.
        /// </summary>
        [Test]
        public async Task Restore_WaitsForAWriteUnderWayEvenInRollbackJournalMode()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 2000);
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await ScalarAsync(connection, "PRAGMA journal_mode = DELETE;");
            }

            Task restore;
            await using (var page = new HostPingerDbContext(_options))
            {
                await page.Database.OpenConnectionAsync();
                await page.Database.ExecuteSqlRawAsync("BEGIN IMMEDIATE;");
                await page.Database.ExecuteSqlRawAsync("UPDATE PingAttempts SET RoundtripMs = 1000;");

                restore = Task.Run(() => _backup.RestoreAsync(backupPath));
                await WaitUntilAsync(() => _fileGate.IsShut, "the restore asks for the gate");
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                Assert.That(restore.IsCompleted, Is.False, "the restore waits for the write");
                await page.Database.ExecuteSqlRawAsync("COMMIT;");
            }

            await restore.WaitAsync(Patience);

            Assert.Multiple(async () =>
            {
                Assert.That(await IntegrityCheckAsync(_dbPath), Is.EqualTo("ok"));
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example" }));
                Assert.That(await ScalarAsync(_dbPath, "SELECT COUNT(*) FROM PingAttempts WHERE RoundtripMs < 1000;"), Is.EqualTo(3L),
                    "the restored file's history is its own");
                Assert.That(await ScalarAsync(_retired, "SELECT COUNT(*) FROM PingAttempts WHERE RoundtripMs = 1000;"),
                    Is.EqualTo(2000L), "the write committed, to the file retired");
            });
        }

        /// <summary>
        /// A host's delete takes its history in batches, each committed on its own, and used to let
        /// go of its connection between them. A restore that swapped the file meanwhile had the
        /// remaining batches take the restored file's host with that id, a different host; and the
        /// delete's connection, back in the pool between batches, held the file so that the restore
        /// could rarely get it anyway. Now the delete keeps its connection to the end, and a restore
        /// waits for it and then swaps a file the delete has finished with. The delete is held at
        /// its first statement by a write lock from outside the gate, until the restore is waiting.
        /// </summary>
        [Test]
        public async Task Restore_WaitsForAHostDeleteUnderWayAndLeavesTheRestoredHostsAlone()
        {
            // Four batches' worth, so the delete has statements left after its first.
            await SeedHostWithAttemptsAsync(_options, "doomed.example", attempts: 4 * HostStore.DeleteBatchSize);
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");
            await _store.LoadAsync();
            await _summaries.LoadAsync();
            await _summaries.StartAsync(CancellationToken.None);
            var hosts = new HostStore(new TestDb.Factory(_options), _summaries, NullLogger<HostStore>.Instance);
            var id = _summaries.Current.Rows.Single().Host.Id;

            Task restore;
            Task delete;
            await using (var writeLock = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
            {
                await writeLock.OpenAsync();
                await ScalarAsync(writeLock, "BEGIN IMMEDIATE;");
                delete = hosts.DeleteAsync(id);
                await WaitUntilAsync(() => _fileGate.OpenConnections == 1, "the delete has its connection open");

                restore = Task.Run(() => _backup.RestoreAsync(backupPath));
                await WaitUntilAsync(() => _fileGate.IsShut, "the restore asks for the gate");
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                Assert.That(restore.IsCompleted, Is.False, "the restore waits for the delete");
                await ScalarAsync(writeLock, "ROLLBACK;");
            }

            await delete.WaitAsync(Patience);
            await restore.WaitAsync(Patience);
            await _summaries.StopAsync(CancellationToken.None);

            Assert.That(id, Is.EqualTo(1), "the host deleted has the id the backup's host has");
            Assert.Multiple(async () =>
            {
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example" }));
                Assert.That(await ScalarAsync(_dbPath, "SELECT COUNT(*) FROM PingAttempts;"), Is.EqualTo(3L),
                    "the restored host keeps its history");
                Assert.That(await AddressesAsync(_retired), Is.Empty, "the delete finished in the file retired");
                Assert.That(await ScalarAsync(_retired, "SELECT COUNT(*) FROM PingAttempts;"), Is.EqualTo(0L));
            });
        }

        /// <summary>
        /// A settings save reads the rows it changes, writes them, and then updates the snapshot of
        /// settings in force, which a restore reloads from the restored file once it is in place. A
        /// restore that swapped the file partway through used to leave the save writing to the
        /// restored file rows it had read from the other, and the snapshot describing either file.
        /// Now the save keeps one connection throughout, so the restore waits for it: the save goes
        /// to the file retired, and what is in force afterwards is the restored file's. Here the
        /// interval is stored in the current file and not in the backup, which is what made the
        /// save's update fail against the restored file.
        /// </summary>
        [Test]
        public async Task Restore_LoadsTheRestoredSettingsOverASaveThatStartedBeforeIt()
        {
            await StagePasswordAsync(_options, "current-hash");
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");
            DatabaseBackup? backup = null;
            Task? restore = null;
            var store = new UserSettingsStore(
                new TestDb.Factory(_options),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions()),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()))
            {
                AfterStage = async () =>
                {
                    if (restore is null && backup is not null)
                    {
                        restore = Task.Run(() => backup.RestoreAsync(backupPath));
                        await WaitUntilAsync(() => _fileGate.IsShut, "the restore asks for the gate");
                    }
                },
            };
            await store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 20 });
            await store.LoadAsync();
            backup = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                BeforeDeletingRetired = KeepWhatWasRetiredAsync,
            };

            await store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 77 });
            await restore!.WaitAsync(Patience);

            var defaults = new PingerOptions();
            Assert.Multiple(async () =>
            {
                Assert.That(store.PasswordHash, Is.EqualTo("backup-hash"));
                Assert.That(store.CurrentPinger.IntervalSeconds, Is.EqualTo(defaults.IntervalSeconds),
                    "the backup stores no interval, so the default is in force");
                Assert.That(await ScalarAsync(_dbPath, "SELECT COUNT(*) FROM UserSettings WHERE Key = 'Pinger:IntervalSeconds';"),
                    Is.EqualTo(0L), "and the restored file still stores none");
                Assert.That(await ScalarAsync(_retired, "SELECT Value FROM UserSettings WHERE Key = 'Pinger:IntervalSeconds';"),
                    Is.EqualTo("77"), "the save went to the file retired");
            });
        }

        /// <summary>
        /// A page that keeps a connection open for the whole of the restore's wait stops the restore
        /// before anything is replaced, as a connection from outside does, and the gate opens again
        /// afterwards rather than leaving every page waiting at it.
        /// </summary>
        [Test]
        public async Task Restore_RefusesWhileAPageKeepsItsConnectionOpenAndOpensTheGateAgain()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");
            var backup = new DatabaseBackup(
                new PingerPaths(_dbPath),
                new TestDb.Factory(_options),
                _store,
                new MaintenanceGate(),
                _fileGate,
                _summaries,
                NullLogger<DatabaseBackup>.Instance)
            {
                BusyTimeoutSeconds = 1,
            };

            IOException? refusal;
            await using (var page = new HostPingerDbContext(_options))
            {
                await page.Database.OpenConnectionAsync();
                refusal = await Assert.ThrowsAsync<IOException>(() => backup.RestoreAsync(backupPath));
            }

            Assert.That(refusal!.Message, Is.EqualTo("The database stayed in use, so nothing was replaced. Try the restore again."));
            Assert.Multiple(async () =>
            {
                Assert.That(_fileGate.IsShut, Is.False);
                Assert.That(await AddressesAsync(_dbPath).WaitAsync(Patience), Is.EqualTo(new[] { "current.example" }),
                    "the database was not replaced, and is open to the pages again");
                Assert.That(File.Exists(backup.PreRestorePath), Is.False);
            });
        }

        /// <summary>
        /// Validation vouches for the migration history, and a file can carry that without the
        /// tables it describes. Swapped in, such a file used to stay in place with the settings
        /// unreadable, and the service could not start on it until somebody put the undo copy back
        /// by hand. Now it is found out where it lies, before anything is replaced.
        /// </summary>
        [Test]
        public async Task Restore_RefusesABackupThatLacksATable_ReplacingNothing()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();

            var backupPath = Path.Combine(_directory, "uploaded.db");
            var backupOptions = OptionsFor(backupPath);
            await using (var db = new HostPingerDbContext(backupOptions))
            {
                await db.Database.MigrateAsync();
            }

            await StagePasswordAsync(backupOptions, "backup-hash");
            await using (var db = new HostPingerDbContext(backupOptions))
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE ResolverErrors;");
            }

            Assert.That(await _backup.ValidateBackupAsync(backupPath), Is.Null, "nothing up front looks at the tables");

            var failure = await Assert.ThrowsAsync<IOException>(() => _backup.RestoreAsync(backupPath));

            await AssertNothingWasReplacedAsync(failure!, "The backup has no ResolverErrors table. Nothing was replaced.");
        }

        /// <summary>
        /// An older backup is migrated before it is restored, and a migration can fail on it —
        /// here the last one, which drops an index the file no longer has. That is found out
        /// before the swap, so the database it was meant to replace never leaves.
        /// </summary>
        [Test]
        public async Task Restore_RefusesABackupThatCannotBeMigrated_ReplacingNothing()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();

            var backupPath = Path.Combine(_directory, "uploaded.db");
            await using (var db = new HostPingerDbContext(OptionsFor(backupPath)))
            {
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM __EFMigrationsHistory WHERE MigrationId = (SELECT MAX(MigrationId) FROM __EFMigrationsHistory);");
                await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_PingAttempts_Answered;");
            }

            Assert.That(await _backup.ValidateBackupAsync(backupPath), Is.Null, "an older backup is accepted");

            var failure = await Assert.ThrowsAsync<IOException>(() => _backup.RestoreAsync(backupPath));

            Assert.That(failure!.Message, Does.Contain("no such index: IX_PingAttempts_Answered"));
            Assert.That(failure.Message, Does.EndWith(" Nothing was replaced."));
            await AssertNothingWasReplacedAsync(failure, failure.Message);
        }

        /// <summary>
        /// A restore checks for triggers and views itself rather than trusting its caller to have
        /// validated the file: migrating it would run a trigger, and swapped in, one on the settings
        /// table outlasts both ways back from a password. Restored here without validating first.
        /// </summary>
        [Test]
        public async Task Restore_RefusesAFileDefiningATrigger_EvenUnvalidated()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");
            await using (var db = new HostPingerDbContext(OptionsFor(backupPath)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER keep_password AFTER UPDATE ON UserSettings BEGIN "
                    + "UPDATE UserSettings SET Value = 'backup-hash' WHERE Key = 'Security:PasswordHash'; END;");
            }

            SqliteConnection.ClearAllPools();
            var failure = await Assert.ThrowsAsync<IOException>(() => _backup.RestoreAsync(backupPath));

            await AssertNothingWasReplacedAsync(
                failure!, "This file defines triggers or views, which a HostPinger database never has. Nothing was replaced.");
        }

        /// <summary>
        /// A restored file that will not open once it is in place — standing in for the settings
        /// failing to load, or the disk filling as it is opened — is taken out again, and the
        /// database it replaced goes back.
        /// </summary>
        [Test]
        public async Task Restore_PutsTheDatabaseBackWhenTheRestoredFileWillNotOpen()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");

            var failure = await Assert.ThrowsAsync<IOException>(() => FailingToOpen().RestoreAsync(backupPath));

            await AssertTheCurrentDatabaseIsBackAsync(failure!, "Opening it failed in the test. The previous database is back in place.");
        }

        /// <summary>
        /// On a fresh install there is nothing to put back, so a backup that will not open leaves
        /// an empty database rather than none — and not the undo an earlier restore left beside it,
        /// which belongs to some other moment entirely.
        /// </summary>
        [Test]
        public async Task Restore_IntoAFreshInstallLeavesAnEmptyDatabaseWhenTheBackupWillNotOpen()
        {
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");

            SqliteConnection.ClearAllPools();
            File.Move(_dbPath, _backup.PreRestorePath);
            await SeedHostWithAttemptsAsync(OptionsFor(_backup.PreRestorePath), "an-earlier-undo.example", attempts: 1);
            SqliteConnection.ClearAllPools();

            var failure = await Assert.ThrowsAsync<IOException>(() => FailingToOpen().RestoreAsync(backupPath));

            await using var live = new HostPingerDbContext(_options);
            await using var undo = new HostPingerDbContext(OptionsFor(_backup.PreRestorePath));
            Assert.Multiple(async () =>
            {
                Assert.That(failure!.Message, Is.EqualTo(
                    "Opening it failed in the test. An empty database is in its place, as there was none before."));
                Assert.That(await live.Database.GetPendingMigrationsAsync(), Is.Empty);
                Assert.That(await live.Hosts.CountAsync(), Is.Zero);
                Assert.That(await undo.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("an-earlier-undo.example"),
                    "the earlier undo is left where it was");
            });
        }

        /// <summary>
        /// Leaving the page once the backup is in place no longer stops the restore: given up
        /// there, it would leave the backup's file under the settings and password of the one it
        /// replaced. The page's token is cancelled at exactly that moment, and the restore still
        /// finishes, with the backup's password in force.
        /// </summary>
        [Test]
        public async Task Restore_FinishesWhenThePageLeavesOnceTheBackupIsInPlace()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();
            var backupPath = await BackupWithAsync("backup.example", "backup-hash");

            using var page = new CancellationTokenSource();
            var backup = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                AfterSwap = () => page.CancelAsync(),
            };

            await backup.RestoreAsync(backupPath, page.Token);

            SqliteConnection.ClearAllPools();
            await using var live = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(page.IsCancellationRequested, Is.True, "the page left");
                Assert.That(await live.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("backup.example"));
                Assert.That(_store.PasswordHash, Is.EqualTo("backup-hash"));
            });
        }

        /// <summary>
        /// An older backup is migrated where it lies, without the maintenance gate: on a
        /// production-sized file that takes minutes, and holding the gate meanwhile left a gap of
        /// that length in every host's history. Here the test holds the gate, as a round would, and
        /// the backup is still brought up to date; only the swap waits.
        /// </summary>
        [Test]
        public async Task Restore_MigratesAnOlderBackupWithoutHoldingUpThePingRounds()
        {
            var backupPath = Path.Combine(_directory, "uploaded.db");
            await using (var db = new HostPingerDbContext(OptionsFor(backupPath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastIn1_12);
            }

            int allMigrations;
            await using (var db = new HostPingerDbContext(_options))
            {
                allMigrations = db.Database.GetMigrations().Count();
            }

            var gate = new MaintenanceGate();
            var backup = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, gate, _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance);
            Task restoring;
            using (await gate.AcquireAsync())
            {
                restoring = backup.RestoreAsync(backupPath);

                var deadline = DateTime.UtcNow + Patience;
                while (await AppliedMigrationsAsync(backupPath) < allMigrations)
                {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the backup is migrated while a round holds the gate");
                    await Task.Delay(50);
                }

                Assert.That(restoring.IsCompleted, Is.False, "the swap waits for the round");
            }

            await restoring.WaitAsync(Patience);
            SqliteConnection.ClearAllPools();
            await using var live = new HostPingerDbContext(_options);
            Assert.That(await live.Database.GetPendingMigrationsAsync(), Is.Empty);
        }

        /// <summary>
        /// Migrating an older backup needs room beside the database for the journal, which on a
        /// production-sized file reached about a third of it. Without that room the backup is
        /// refused up front rather than filling the disk partway through.
        /// </summary>
        [Test]
        public async Task ValidateBackup_RefusesAnOlderBackupWithoutRoomToMigrateIt()
        {
            var backupPath = Path.Combine(_directory, "uploaded.db");
            await using (var db = new HostPingerDbContext(OptionsFor(backupPath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastIn1_12);
            }

            var cramped = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                MeasureFreeBytes = () => 3L << 20,
            };

            Assert.That(await cramped.ValidateBackupAsync(backupPath), Is.EqualTo(
                "This backup is from an older version, and migrating it needs about 64 MB free beside the database. The disk has 3 MB."));
        }

        /// <summary>A backup with nothing to migrate needs no room beyond the upload it already is.</summary>
        [Test]
        public async Task ValidateBackup_AcceptsACurrentBackupWhateverRoomIsLeft()
        {
            var backupPath = await _backup.CreateSnapshotAsync();
            var cramped = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                MeasureFreeBytes = () => 0,
            };

            Assert.That(await cramped.ValidateBackupAsync(backupPath), Is.Null);
        }

        /// <summary>
        /// What a restore refused before the swap must leave: the current database untouched at
        /// the path with its settings in force, no undo copy made, and the failure saying why.
        /// </summary>
        private async Task AssertNothingWasReplacedAsync(IOException failure, string expectedMessage)
        {
            SqliteConnection.ClearAllPools();
            await using var live = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(failure.Message, Is.EqualTo(expectedMessage));
                Assert.That(await live.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("current.example"));
                Assert.That(await live.PingAttempts.CountAsync(), Is.EqualTo(1));
                Assert.That(_store.PasswordHash, Is.EqualTo("current-hash"), "the settings in force are the current database's");
                Assert.That(File.Exists(_backup.PreRestorePath), Is.False, "nothing was retired");
            });
        }

        /// <summary>
        /// The page hands the browser's upload over as it is read, and the backup does the rest in
        /// one turn: receives the file beside the database, checks it and puts it in place, saying
        /// which it is on, the upload by how far it has got. Nothing of the upload is left.
        /// </summary>
        [Test]
        public async Task RestoreUpload_ReceivesChecksAndRestoresTheFileSayingWhichStepItIsOn()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", BackupHash)));
            var reports = new List<RestoreProgress>();

            var refusal = await _backup.RestoreUploadAsync(upload, upload.Size, BackupPassword, new Recorder<RestoreProgress>(reports.Add));

            var received = reports.Where(r => r.Step == RestoreStep.Receiving).Select(r => r.Fraction).ToList();
            Assert.Multiple(async () =>
            {
                Assert.That(refusal, Is.Null);
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example" }));
                Assert.That(_store.PasswordHash, Is.EqualTo(BackupHash));
                Assert.That(reports.Select(r => r.Step).Distinct(), Is.EqualTo(new[]
                {
                    RestoreStep.Receiving, RestoreStep.Checking, RestoreStep.Replacing,
                }));
                Assert.That(received, Is.Ordered.And.Not.Empty);
                Assert.That(received[^1], Is.EqualTo(1));
                Assert.That(received.Count(fraction => fraction is > 0 and < 1), Is.Positive, "partway, not only before and after");
                Assert.That(reports.Where(r => r.Step != RestoreStep.Receiving).Select(r => r.Fraction), Has.All.Null);
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-restore-*"), Is.Empty);
            });
        }

        /// <summary>A file the check turns away is refused in so many words, and none of it is kept.</summary>
        [Test]
        public async Task RestoreUpload_RefusesAFileThatFailsTheCheckAndKeepsNothingOfIt()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var upload = new UploadStream(Enumerable.Repeat((byte)0x2A, 100_000).ToArray());

            var refusal = await _backup.RestoreUploadAsync(upload, upload.Size, BackupPassword);

            Assert.Multiple(async () =>
            {
                Assert.That(refusal, Is.EqualTo(new RestoreRefusal("This file is not an SQLite database.")));
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "current.example" }));
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-restore-*"), Is.Empty);
            });
        }

        /// <summary>
        /// Without room beside the database for the file, the restore is refused before any of the
        /// upload is read: the page used to check this itself, out of turn, where two uploads at
        /// once could each count on the same room.
        /// </summary>
        [Test]
        public async Task RestoreUpload_RefusesWithoutRoomForTheFileBeforeReadingAnyOfIt()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", BackupHash)));
            var cramped = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                MeasureFreeBytes = () => 3L << 20,
            };

            var refusal = await cramped.RestoreUploadAsync(upload, upload.Size, BackupPassword);

            Assert.Multiple(async () =>
            {
                Assert.That(refusal?.Message, Is.EqualTo(
                    "Receiving the backup needs about 64 MB free beside the database. The disk has 3 MB."));
                Assert.That(upload.Position, Is.Zero, "nothing of the upload was read");
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "current.example" }));
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-restore-*"), Is.Empty);
            });
        }

        /// <summary>
        /// An upload that stops partway — the browser can no longer read the file — fails the
        /// restore with what the browser said, leaves nothing of what had arrived, and gives the
        /// turn back for the next backup or restore.
        /// </summary>
        [Test]
        public async Task RestoreUpload_DeletesWhatItReceivedWhenTheUploadFails()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", BackupHash)), fail: true);

            var failure = await Assert.ThrowsAsync<IOException>(() => _backup.RestoreUploadAsync(upload, upload.Size, BackupPassword));

            Assert.Multiple(async () =>
            {
                Assert.That(failure!.Message, Is.EqualTo("The browser could not read the file."));
                Assert.That(upload.Position, Is.Positive, "it failed partway, not before the upload began");
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "current.example" }));
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-restore-*"), Is.Empty);
                Assert.That(File.Exists(await _backup.CreateSnapshotAsync().WaitAsync(Patience)), "the turn was given back");
            });
        }

        /// <summary>
        /// The room was checked for the length the page gave, so more arriving than that is a
        /// failure rather than something to go on writing until the disk is full.
        /// </summary>
        [Test]
        public async Task RestoreUpload_RefusesMoreThanTheLengthItWasGiven()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", BackupHash)));

            var failure = await Assert.ThrowsAsync<IOException>(() => _backup.RestoreUploadAsync(upload, 50_000, BackupPassword));

            Assert.Multiple(async () =>
            {
                Assert.That(failure!.Message, Is.EqualTo("More arrived than the 50,000 bytes the file was said to hold."));
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "current.example" }));
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-restore-*"), Is.Empty);
            });
        }

        /// <summary>
        /// A backup that puts a password in force is only restored with that password, so that
        /// whoever restores it can unlock HostPinger afterwards. None given, or the wrong one, is
        /// refused as a password, the refusal the page asks again after, with nothing replaced and
        /// nothing of the upload kept.
        /// </summary>
        [TestCase(null, "The backup has a password. Enter it to restore the backup.")]
        [TestCase("", "The backup has a password. Enter it to restore the backup.")]
        [TestCase("not the backup's password", "That is not the backup's password.")]
        public async Task RestoreUpload_RefusesAPasswordMissingOrWrongAndReplacesNothing(string? password, string expected)
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            await StagePasswordAsync(_options, "current-hash");
            await _store.LoadAsync();
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", BackupHash)));

            var refusal = await _backup.RestoreUploadAsync(upload, upload.Size, password);

            Assert.Multiple(async () =>
            {
                Assert.That(refusal, Is.EqualTo(new RestoreRefusal(expected, IsPassword: true)));
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "current.example" }));
                Assert.That(_store.PasswordHash, Is.EqualTo("current-hash"), "the settings in force are the current database's");
                Assert.That(File.Exists(_backup.PreRestorePath), Is.False, "nothing was retired");
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-restore-*"), Is.Empty);
            });
        }

        /// <summary>
        /// A backup whose password was removed puts none in force, even on an install that
        /// configures one, since the removal is a stored row and a stored row wins. So it is
        /// restored without a password, whatever is typed, and leaves nothing locked.
        /// </summary>
        [TestCase(null)]
        [TestCase("whatever is typed")]
        public async Task RestoreUpload_NeedsNoPasswordForABackupWhosePasswordWasRemoved(string? password)
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var (store, backup) = ConfiguredWith(PasswordHash.Hash("configured"));
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", string.Empty)));

            var refusal = await backup.RestoreUploadAsync(upload, upload.Size, password);

            Assert.Multiple(async () =>
            {
                Assert.That(refusal, Is.Null);
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example" }));
                Assert.That(store.PasswordHash, Is.Empty, "no password in force");
            });
        }

        /// <summary>
        /// A backup with no password row puts this install's configured password in force once its
        /// settings load, so that is the password it takes — not the one the backup never had.
        /// </summary>
        [Test]
        public async Task RestoreUpload_TakesTheConfiguredPasswordForABackupStoringNone()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var configured = PasswordHash.Hash("configured");
            var (store, backup) = ConfiguredWith(configured);
            var bytes = await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", hash: null));

            var wrong = await backup.RestoreUploadAsync(new UploadStream(bytes), bytes.Length, BackupPassword);
            var right = await backup.RestoreUploadAsync(new UploadStream(bytes), bytes.Length, "configured");

            Assert.Multiple(async () =>
            {
                Assert.That(wrong, Is.EqualTo(new RestoreRefusal("That is not the backup's password.", IsPassword: true)));
                Assert.That(right, Is.Null);
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "backup.example" }));
                Assert.That(store.PasswordHash, Is.EqualTo(configured));
            });
        }

        /// <summary>
        /// A backup older than the settings table has none to read a password from, which is not a
        /// failure: like any backup storing no password, it puts this install's configured one in
        /// force, and takes that. The table only comes with the migrations, after the password is
        /// checked.
        /// </summary>
        [Test]
        public async Task RestoreUpload_TakesTheConfiguredPasswordForABackupOlderThanTheSettingsTable()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var backupPath = Path.Combine(_directory, "uploaded.db");
            await using (var db = new HostPingerDbContext(OptionsFor(backupPath)))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260811051236_AddResolverErrors");
            }

            await SeedHostWithAttemptsAsync(OptionsFor(backupPath), "older.example", attempts: 1);
            SqliteConnection.ClearAllPools();
            Assert.That(await ScalarAsync(backupPath, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'UserSettings';"), Is.Zero,
                "the backup is older than the settings table");

            var configured = PasswordHash.Hash("configured");
            var (store, backup) = ConfiguredWith(configured);
            var bytes = await File.ReadAllBytesAsync(backupPath);

            var none = await backup.RestoreUploadAsync(new UploadStream(bytes), bytes.Length, password: null);
            var right = await backup.RestoreUploadAsync(new UploadStream(bytes), bytes.Length, "configured");

            Assert.Multiple(async () =>
            {
                Assert.That(none, Is.EqualTo(new RestoreRefusal("The backup has a password. Enter it to restore the backup.", IsPassword: true)));
                Assert.That(right, Is.Null);
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "older.example" }));
                Assert.That(store.PasswordHash, Is.EqualTo(configured));
            });
        }

        /// <summary>
        /// While one restore is under way, another restore or a backup is turned away before it
        /// starts. A second restore used to go ahead alongside, counting on the room the first had
        /// counted on, and finishing second it retired the first one's file as the undo copy in
        /// place of the database both had meant to replace.
        /// </summary>
        [TestCase("restore")]
        [TestCase("backup")]
        public async Task WhileARestoreIsUnderWay_AnotherIsTurnedAway(string asked)
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var first = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("first.example", BackupHash)), hold: true);
            var second = new UploadStream(
                await File.ReadAllBytesAsync(await BackupWithAsync("second.example", BackupHash, "second.db")));
            var restoring = _backup.RestoreUploadAsync(first, first.Size, BackupPassword);
            await first.Held.Task.WaitAsync(Patience);

            var refusal = asked == "restore"
                ? (await _backup.RestoreUploadAsync(second, second.Size, BackupPassword))?.Message
                : (await Assert.ThrowsAsync<IOException>(() => _backup.CreateSnapshotAsync()))!.Message;
            first.Release();

            Assert.That(await restoring.WaitAsync(Patience), Is.Null);
            Assert.Multiple(async () =>
            {
                Assert.That(refusal, Is.EqualTo("A restore is under way. Try again once it has finished."));
                Assert.That(second.Position, Is.Zero, "nothing of the second upload was read");
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "first.example" }));
                Assert.That(await AddressesAsync(_retired), Is.EqualTo(new[] { "current.example" }),
                    "what the restore replaced was the database both meant to replace");
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-backup-*"), Is.Empty, "no snapshot was written");
            });
        }

        /// <summary>
        /// While a backup is being written, a restore or another backup is turned away before it
        /// starts, rather than writing a second file beside the database on the room the first one
        /// counted on. The backup is held at its last report of progress, which comes before it
        /// gives its turn back.
        /// </summary>
        [TestCase("restore")]
        [TestCase("backup")]
        public async Task WhileABackupIsBeingWritten_AnotherIsTurnedAway(string asked)
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var upload = new UploadStream(await File.ReadAllBytesAsync(await BackupWithAsync("backup.example", BackupHash)));
            using var held = new ManualResetEventSlim();
            var reporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writing = _backup.CreateSnapshotAsync(new Recorder<double>(fraction =>
            {
                if (fraction == 1)
                {
                    reporting.TrySetResult();
                    held.Wait(Patience);
                }
            }));
            await reporting.Task.WaitAsync(Patience);

            var refusal = asked == "restore"
                ? (await _backup.RestoreUploadAsync(upload, upload.Size, BackupPassword))?.Message
                : (await Assert.ThrowsAsync<IOException>(() => _backup.CreateSnapshotAsync()))!.Message;
            held.Set();
            var snapshot = await writing.WaitAsync(Patience);

            Assert.Multiple(async () =>
            {
                Assert.That(refusal, Is.EqualTo("A backup is being written. Try again once it has finished."));
                Assert.That(upload.Position, Is.Zero, "nothing of the upload was read");
                Assert.That(await AddressesAsync(_dbPath), Is.EqualTo(new[] { "current.example" }), "nothing was restored");
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-backup-*"), Is.EqualTo(new[] { snapshot }),
                    "and no second snapshot written");
            });
        }

        /// <summary>
        /// Without room beside the database for the copy, the backup is refused before it writes
        /// anything, in so many words, which the page shows as the reason it could not be written.
        /// </summary>
        [Test]
        public async Task CreateSnapshot_RefusesWithoutRoomForTheCopyAndWritesNothing()
        {
            await SeedHostWithAttemptsAsync(_options, "current.example", attempts: 1);
            var cramped = new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                MeasureFreeBytes = () => 3L << 20,
            };

            var refusal = await Assert.ThrowsAsync<IOException>(() => cramped.CreateSnapshotAsync());

            Assert.Multiple(() =>
            {
                Assert.That(refusal!.Message, Is.EqualTo(
                    "Writing the snapshot needs about 64 MB free beside the database. The disk has 3 MB."));
                Assert.That(Directory.EnumerateFiles(_directory, "hostpinger-backup-*"), Is.Empty);
            });
        }

        /// <summary>
        /// What a round coming due does, from a snapshot's report of progress: waits up to
        /// <paramref name="patience"/> for the gate and runs <paramref name="write"/> holding it,
        /// "ran", or gives up, "held off".
        /// </summary>
        private static string RoundPartwayThrough(MaintenanceGate gate, TimeSpan patience, Action write)
        {
            using var giveUp = new CancellationTokenSource(patience);
            try
            {
                using (gate.AcquireAsync(giveUp.Token).GetAwaiter().GetResult())
                {
                    write();
                }

                return "ran";
            }
            catch (OperationCanceledException)
            {
                return "held off";
            }
        }

        /// <summary>Snapshots that say how far they have got every <paramref name="interval"/>.</summary>
        private DatabaseBackup SnapshotsWatchedEvery(TimeSpan interval) =>
            new(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                SnapshotProgressInterval = interval,
            };

        /// <summary>
        /// What the restores here do just before deleting the database they replaced: copy it to
        /// <see cref="_retired"/>, for the tests that look at what became of a write that raced
        /// the restore.
        /// </summary>
        private Task KeepWhatWasRetiredAsync()
        {
            File.Copy(_dbPath + ".pre-restore", _retired, overwrite: true);
            return Task.CompletedTask;
        }

        /// <summary>
        /// A restore onto an install that configures <paramref name="configured"/> as its password,
        /// in appsettings.json or the environment, with the store that holds the settings it loads.
        /// </summary>
        private (UserSettingsStore Store, DatabaseBackup Backup) ConfiguredWith(string? configured)
        {
            var store = new UserSettingsStore(
                new TestDb.Factory(_options),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions()),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions { PasswordHash = configured }));
            return (store, new DatabaseBackup(new PingerPaths(_dbPath), new TestDb.Factory(_options), store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance));
        }

        /// <summary>A restore whose backup goes into place and then fails to open, for the put-back.</summary>
        private DatabaseBackup FailingToOpen() =>
            new(new PingerPaths(_dbPath), new TestDb.Factory(_options), _store, new MaintenanceGate(), _fileGate, _summaries, NullLogger<DatabaseBackup>.Instance)
            {
                AfterSwap = () => throw new InvalidDataException("Opening it failed in the test."),
            };

        /// <summary>
        /// A current backup holding one host and a password, as an upload would be; with a
        /// <paramref name="hash"/> of null, no password row at all.
        /// </summary>
        private async Task<string> BackupWithAsync(string address, string? hash, string fileName = "uploaded.db")
        {
            var backupPath = Path.Combine(_directory, fileName);
            var backupOptions = OptionsFor(backupPath);
            await using (var db = new HostPingerDbContext(backupOptions))
            {
                await db.Database.MigrateAsync();
            }

            await SeedHostWithAttemptsAsync(backupOptions, address, attempts: 3);
            if (hash is not null)
            {
                await StagePasswordAsync(backupOptions, hash);
            }

            SqliteConnection.ClearAllPools();
            return backupPath;
        }

        /// <summary>
        /// How many migrations a file records, read on a connection of its own that a restore
        /// working on the same file may keep waiting for a moment.
        /// </summary>
        private static async Task<long> AppliedMigrationsAsync(string path)
        {
            try
            {
                await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=1");
                await connection.OpenAsync();
                return Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory;"));
            }
            catch (SqliteException)
            {
                return 0;
            }
        }

        /// <summary>
        /// What a restore that failed after the swap must leave: the database it was meant to
        /// replace back at the path, whole and in rollback-journal mode, with its settings in force
        /// and no undo copy left over, and the failure saying why.
        /// </summary>
        private async Task AssertTheCurrentDatabaseIsBackAsync(IOException failure, string expectedMessage)
        {
            SqliteConnection.ClearAllPools();
            await using var live = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(failure.Message, Is.EqualTo(expectedMessage));
                Assert.That(await live.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("current.example"));
                Assert.That(await live.PingAttempts.CountAsync(), Is.EqualTo(1));
                Assert.That(_store.PasswordHash, Is.EqualTo("current-hash"), "the settings in force are the current database's");
                Assert.That(File.Exists(_backup.PreRestorePath), Is.False, "the file kept to put back is the live database again");
                Assert.That(File.Exists(_dbPath + "-wal"), Is.False, "nothing of the restored file's is left beside it");
                Assert.That(await JournalModeAsync(_dbPath), Is.EqualTo("delete"));
                Assert.That(await IntegrityCheckAsync(_dbPath), Is.EqualTo("ok"));
            });
        }

        private static DbContextOptions<HostPingerDbContext> OptionsFor(string path, DatabaseFileGate? gate = null)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite($"Data Source={path}");
            if (gate is not null)
            {
                options.AddInterceptors(gate.Interceptor);
            }

            return options.Options;
        }

        private static async Task SeedHostWithAttemptsAsync(
            DbContextOptions<HostPingerDbContext> options, string address, int attempts)
        {
            await using var db = new HostPingerDbContext(options);
            var host = new MonitoredHost { Name = address, Address = address };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();

            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            db.PingAttempts.AddRange(Enumerable.Range(0, attempts).Select(i => new PingAttempt
            {
                HostId = host.Id,
                TimestampUtc = start.AddSeconds(i),
                RoundtripMs = i % 100,
            }));
            await db.SaveChangesAsync();
        }

        /// <summary>Adds about <paramref name="megabytes"/> of rows, in a table of their own.</summary>
        private static async Task FillAsync(string path, int megabytes)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Filler (Data BLOB);"
                + "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < $rows) "
                + "INSERT INTO Filler SELECT randomblob(4000) FROM n;";
            command.Parameters.AddWithValue("$rows", megabytes * 256);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task StagePasswordAsync(DbContextOptions<HostPingerDbContext> options, string hash)
        {
            await using var db = new HostPingerDbContext(options);
            await UserSettingsStore.StageAsync(db, UserSettingsStore.PasswordHashKey, hash);
            await db.SaveChangesAsync();
        }

        private static async Task<string?> JournalModeAsync(string path)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
            await connection.OpenAsync();
            return (string?)await ScalarAsync(connection, "PRAGMA journal_mode;");
        }

        /// <summary>
        /// Waits for <paramref name="condition"/>, which another thread brings about, failing the
        /// test with <paramref name="because"/> if it never does.
        /// </summary>
        private static async Task WaitUntilAsync(Func<bool> condition, string because)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!condition())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), because);
                await Task.Delay(10);
            }
        }

        /// <summary>The hosts' addresses in the file at <paramref name="path"/>, read from outside the gate.</summary>
        private static async Task<List<string>> AddressesAsync(string path)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Address FROM Hosts ORDER BY Id;";
            var addresses = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                addresses.Add(reader.GetString(0));
            }

            return addresses;
        }

        /// <summary>One value read from the file at <paramref name="path"/>, from outside the gate.</summary>
        private static async Task<object?> ScalarAsync(string path, string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            return await ScalarAsync(connection, sql);
        }

        private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        private static async Task<string?> IntegrityCheckAsync(string path)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return (string?)await command.ExecuteScalarAsync();
        }

        /// <summary>
        /// A backup's bytes handed over the way a browser's upload is, a piece at a time: held after
        /// the first piece until <see cref="Release"/>, when asked to, or failing there, as an
        /// upload does when the browser can no longer read the file.
        /// </summary>
        private sealed class UploadStream(byte[] bytes, bool hold = false, bool fail = false) : Stream
        {
            private const int Piece = 16 * 1024;

            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _position;

            /// <summary>How long the upload is, which the page is told by the browser.</summary>
            public long Size => bytes.Length;

            /// <summary>Set once the first piece has been read, which is where a held upload waits.</summary>
            public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            /// <summary>How much has been read so far.</summary>
            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public void Release() => _released.TrySetResult();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position > 0)
                {
                    Held.TrySetResult();
                    if (fail)
                    {
                        throw new IOException("The browser could not read the file.");
                    }

                    if (hold)
                    {
                        await _released.Task.WaitAsync(cancellationToken);
                    }
                }

                var count = Math.Min(Math.Min(buffer.Length, Piece), bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// Hears each report as it is made, on the thread that makes it, where Progress&lt;T&gt;
        /// would post them to the thread pool to arrive in any order.
        /// </summary>
        private sealed class Recorder<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }
    }
}
