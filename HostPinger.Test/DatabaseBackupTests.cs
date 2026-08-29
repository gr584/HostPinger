using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    /// <summary>
    /// Exercises <see cref="DatabaseBackup"/> against real files, because what it does — copy,
    /// validate, swap — is exactly the part an in-memory database cannot stand in for.
    /// </summary>
    public class DatabaseBackupTests
    {
        private string _directory = null!;
        private string _dbPath = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;
        private UserSettingsStore _store = null!;
        private DatabaseBackup _backup = null!;

        [SetUp]
        public async Task SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _dbPath = Path.Combine(_directory, "hostpinger.db");
            _options = OptionsFor(_dbPath);

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
            _backup = new DatabaseBackup(
                new PingerPaths(_dbPath),
                new TestDb.Factory(_options),
                _store,
                new MaintenanceGate());
        }

        [TearDown]
        public void TearDown()
        {
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

        [Test]
        public async Task Restore_SwapsFileKeepsPreRestoreAndReloadsSettings()
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
            await using var kept = new HostPingerDbContext(OptionsFor(_backup.PreRestorePath));
            Assert.Multiple(async () =>
            {
                Assert.That(await live.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("backup.example"),
                    "the live path must now answer with the backup's data");
                Assert.That(await live.PingAttempts.CountAsync(), Is.EqualTo(3));
                Assert.That(await kept.Hosts.Select(h => h.Address).SingleAsync(), Is.EqualTo("current.example"),
                    "the replaced database must survive as the undo copy");
                Assert.That(_store.PasswordHash, Is.EqualTo("backup-hash"),
                    "the settings snapshot must already reflect the restored file");
                Assert.That(File.Exists(backupPath), Is.False, "the swap consumes the uploaded file");
            });
        }

        [Test]
        public async Task DeleteLeftoverTempFiles_SweepsOnlyItsOwn()
        {
            var snapshotLeftover = Path.Combine(_directory, "hostpinger-backup-dead.tmp");
            var restoreLeftover = Path.Combine(_directory, "hostpinger-restore-dead.tmp");
            await File.WriteAllTextAsync(snapshotLeftover, "left behind by a crash");
            await File.WriteAllTextAsync(restoreLeftover, "left behind by a crash");

            DatabaseBackup.DeleteLeftoverTempFiles(new PingerPaths(_dbPath));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(snapshotLeftover), Is.False);
                Assert.That(File.Exists(restoreLeftover), Is.False);
                Assert.That(File.Exists(_dbPath), "the database itself is not a leftover");
            });
        }

        private static DbContextOptions<HostPingerDbContext> OptionsFor(string path) =>
            new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;

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

        private static async Task StagePasswordAsync(DbContextOptions<HostPingerDbContext> options, string hash)
        {
            await using var db = new HostPingerDbContext(options);
            await UserSettingsStore.StageAsync(db, UserSettingsStore.PasswordHashKey, hash);
            await db.SaveChangesAsync();
        }

        private static async Task<string?> IntegrityCheckAsync(string path)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return (string?)await command.ExecuteScalarAsync();
        }
    }
}
