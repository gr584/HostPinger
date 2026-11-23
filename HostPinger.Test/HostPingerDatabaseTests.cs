using System.Globalization;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    /// <summary>
    /// Opening the database: how a path becomes a connection, how the application registers it,
    /// and what has to happen, in what order, before the first page is served. All of it used to
    /// sit in Program, where nothing tested it.
    /// </summary>
    public class HostPingerDatabaseTests
    {
        /// <summary>The last migration before the settings table, for a database an older build left.</summary>
        private const string BeforeUserSettings = "20260811051236_AddResolverErrors";

        /// <summary>The last migration 1.12.0 shipped, with both index rebuilds still to come.</summary>
        private const string LastIn1_12 = "20260829041132_AddUserSettings";

        /// <summary>The last migration with the attempts' times stored as text, 1.13.0's and 1.14.0's.</summary>
        private const string LastWithTextTimes = "20261016020813_AddRoundtripToPartialPingIndexes";

        /// <summary>The format the SQLite provider wrote a DateTime in, which is what an older build left.</summary>
        private const string ProviderTextFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        private string _directory = null!;
        private string _databasePath = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-open-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "hostpinger.db");
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        /// <summary>
        /// A path is a file name, whatever it holds. Written straight into the string, a semicolon
        /// in a directory name ended the data source there and began a keyword the provider has
        /// never heard of.
        /// </summary>
        [Test]
        public async Task Options_OpenTheFileAtThePathEvenWithASemicolonInIt()
        {
            var awkward = Path.Combine(_directory, "data; Mode=ReadOnly", "hostpinger.db");
            Directory.CreateDirectory(Path.GetDirectoryName(awkward)!);

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(awkward)))
            {
                await HostPingerDatabase.InitializeAsync(db);
                db.Hosts.Add(new MonitoredHost { Name = "alpha", Address = "alpha.example", CreatedUtc = Start });
                await db.SaveChangesAsync();
            }

            Assert.That(File.Exists(awkward), Is.True, "the file at the path itself, opened for writing");
        }

        /// <summary>
        /// The factory the application registers opens the database the paths name, which every
        /// reader and writer then shares — the running service's way in, so the awkward path is
        /// checked here as well as through <see cref="HostPingerDatabase.Options"/>.
        /// </summary>
        [Test]
        public async Task AddHostPingerDatabase_RegistersAFactoryOverThePathsDatabaseEvenWithASemicolonInIt()
        {
            var awkward = Path.Combine(_directory, "data; Mode=ReadOnly", "hostpinger.db");
            Directory.CreateDirectory(Path.GetDirectoryName(awkward)!);
            var paths = PingerPaths.Resolve(awkward, _directory);
            await using var services = new ServiceCollection().AddHostPingerDatabase(paths).BuildServiceProvider();

            await using (var db = await services.GetRequiredService<IDbContextFactory<HostPingerDbContext>>().CreateDbContextAsync())
            {
                Assert.That(new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource, Is.EqualTo(awkward));
                await HostPingerDatabase.InitializeAsync(db);
                db.Hosts.Add(new MonitoredHost { Name = "alpha", Address = "alpha.example", CreatedUtc = Start });
                await db.SaveChangesAsync();
            }

            Assert.That(File.Exists(awkward), Is.True);
        }

        /// <summary>
        /// A start killed partway through a migration leaves the migrator's lock row behind — which
        /// is what systemd's start timeout did to an upgrade from 1.12.0, whose two index rebuilds
        /// outlasted it. Every later start then waited on that row forever, and the service only
        /// came back once somebody deleted it by hand.
        /// </summary>
        [Test]
        public async Task InitializeAsync_MigratesPastALockAKilledMigrationLeftBehind()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastIn1_12);
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-10-22 07:03:51.8874913+00:00');");
            }

            // The migrator's wait for the lock honours cancellation, so a lock still in the way
            // fails the test here rather than hanging the run.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var log = new DatabaseLog();
            bool cleared;
            await using (var db = new HostPingerDbContext(log.Options(_databasePath)))
            {
                cleared = await HostPingerDatabase.InitializeAsync(db, timeout.Token);
            }

            await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            var connection = check.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";
            Assert.Multiple(async () =>
            {
                Assert.That(await check.Database.GetPendingMigrationsAsync(), Is.Empty);
                Assert.That(await count.ExecuteScalarAsync(), Is.Zero, "the lock is released once the migrations are done");
                Assert.That(cleared, Is.True);
                Assert.That(log.Entries, Has.Exactly(1).Matches<(LogLevel Level, string Message, Exception? Exception)>(
                    e => e.Level == LogLevel.Warning && e.Message.StartsWith("Cleared the lock an interrupted migration left in ")));
            });
        }

        /// <summary>
        /// A database an older build left is in rollback-journal mode, where a long read holds up a
        /// ping round's write — production's was — and opening it puts it in WAL mode, once it is
        /// migrated, so that an upgrade's index rebuild is journalled the old way. A brand-new
        /// database ends up the same: the application opens the file before EF sees it, so EF
        /// never creates it, and SQLite starts it in rollback-journal mode.
        /// </summary>
        [TestCase(LastIn1_12)]
        [TestCase(null)]
        public async Task InitializeAsync_PutsTheDatabaseInWalModeOnceItIsMigrated(string? leftAt)
        {
            if (leftAt is not null)
            {
                await using var older = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
                await older.GetService<IMigrator>().MigrateAsync(leftAt);

                // EF puts a file it creates itself in WAL mode, which is not how production's came to be.
                await older.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = DELETE;");
                Assert.That(await JournalModeAsync(), Is.EqualTo("delete"), "the mode an older build left it in");
            }

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await HostPingerDatabase.InitializeAsync(db);
                Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
            }

            Assert.That(await JournalModeAsync(), Is.EqualTo("wal"));
        }

        /// <summary>
        /// Every connection the application's contexts open caps what a checkpoint leaves of the
        /// -wal. The pragma is the connection's rather than the file's, so it is asked of each way a
        /// context is made: the application's factory, and the options the command line and the
        /// seeding use.
        /// </summary>
        [Test]
        public async Task Contexts_CapTheWalOnEveryConnectionTheyOpen()
        {
            await using var provider = new ServiceCollection()
                .AddHostPingerDatabase(PingerPaths.Resolve(_databasePath, _directory))
                .BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<HostPingerDbContext>>();

            await using var fromFactory = await factory.CreateDbContextAsync();
            await using var fromOptions = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));

            Assert.Multiple(async () =>
            {
                Assert.That(await JournalSizeLimitAsync(fromFactory), Is.EqualTo(HostPingerDatabase.JournalSizeLimitBytes));
                Assert.That(await JournalSizeLimitAsync(fromOptions), Is.EqualTo(HostPingerDatabase.JournalSizeLimitBytes));
            });
        }

        /// <summary>
        /// Upgrading a database whose attempts' times are text, as every build before this one
        /// wrote them, gives every attempt its time in Unix milliseconds, rounded down: a whole
        /// second the provider wrote with no fraction at all, fractions it shortened by dropping
        /// trailing zeros, times before 1970, where rounding down is away from zero, and the ends
        /// of the range a DateTime has. The attempts keep their Ids and hosts, the table keeps its
        /// AUTOINCREMENT counter, the indexes are there again, and SQLite's own date functions
        /// read the times.
        /// </summary>
        [Test]
        public async Task InitializeAsync_ConvertsEveryAttemptsTimeToItsUnixMillisecond()
        {
            var times = new List<DateTime>
            {
                new(2026, 10, 10, 18, 1, 10, DateTimeKind.Utc),
                new DateTime(2026, 10, 10, 18, 1, 10, DateTimeKind.Utc).AddTicks(5_000_000),
                new DateTime(2026, 10, 10, 18, 1, 10, DateTimeKind.Utc).AddTicks(7_688_978),
                new DateTime(2026, 10, 10, 18, 1, 10, DateTimeKind.Utc).AddTicks(1),
                new DateTime(2000, 2, 29, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999),
                new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_995_000),
                new(1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            };
            var random = new Random(1234);
            for (var i = 0; i < 200; i++)
            {
                times.Add(new DateTime(random.NextInt64(DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc));
            }

            await SeedAtTextTimesAsync(times);

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await HostPingerDatabase.InitializeAsync(db);
            }

            await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            var attempts = await check.PingAttempts.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
            Assert.Multiple(async () =>
            {
                Assert.That(attempts.Select(a => a.Id), Is.EqualTo(Enumerable.Range(1, times.Count).Select(i => (long)i)));
                Assert.That(await StoredValuesAsync(), Is.EqualTo(times.Select(UnixMillisecondsRoundedDown)));
                Assert.That(attempts.Select(a => a.TimestampUtc), Is.EqualTo(times.Select(t => DateTime.UnixEpoch.AddMilliseconds(UnixMillisecondsRoundedDown(t)))));
                Assert.That(attempts.Select(a => a.TimestampUtc.Kind), Is.All.EqualTo(DateTimeKind.Utc), "read back as the UTC it was stored as");
                Assert.That(attempts.Select(a => a.HostId), Is.All.EqualTo(1));
                Assert.That(await ScalarAsync("SELECT datetime(TimestampUtc / 1000, 'unixepoch') FROM PingAttempts WHERE Id = 3;"),
                    Is.EqualTo("2026-10-10 18:01:10"), "SQLite reads the time itself");
                Assert.That(await ScalarAsync("SELECT COUNT(*) FROM PingAttempts WHERE typeof(TimestampUtc) <> 'integer';"), Is.Zero);
                Assert.That(await ScalarAsync("SELECT seq FROM sqlite_sequence WHERE name = 'PingAttempts';"), Is.EqualTo(5000),
                    "the counter carried over, past Ids a deleted attempt had");
                Assert.That(await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'ef_temp%';"), Is.Zero);
                Assert.That(
                    await ScalarAsync(
                        "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'PingAttempts' ORDER BY name);"),
                    Is.EqualTo("IX_PingAttempts_Answered,IX_PingAttempts_HostId_TimestampUtc,IX_PingAttempts_Unanswered"));
                Assert.That(await ScalarAsync("PRAGMA integrity_check;"), Is.EqualTo("ok"));
            });
        }

        /// <summary>
        /// An attempt whose host is gone is left behind rather than failing the upgrade, and with it
        /// every start: foreign keys are enforced as the attempts are copied, and nothing could show
        /// such an attempt anyway. The others are all kept.
        /// </summary>
        [Test]
        public async Task InitializeAsync_LeavesBehindAnAttemptWhoseHostIsGone()
        {
            await SeedAtTextTimesAsync([Start, Start.AddSeconds(10)]);
            // The provider's SQLite enforces foreign keys on every connection unless told not to,
            // which is the only way an attempt like this comes to be.
            await ExecuteAsync(
                "PRAGMA foreign_keys = OFF; INSERT INTO PingAttempts (HostId, TimestampUtc, RoundtripMs) VALUES (99, '2026-10-01 00:00:20', 7);");

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await HostPingerDatabase.InitializeAsync(db);
            }

            await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            Assert.That(
                await check.PingAttempts.AsNoTracking().OrderBy(a => a.Id).Select(a => a.TimestampUtc).ToListAsync(),
                Is.EqualTo(new[] { Start, Start.AddSeconds(10) }));
        }

        /// <summary>
        /// The migration's Down, which the upgrade benchmark takes the database back through, writes
        /// each time back as the text the provider would have written for its millisecond, before
        /// 1970 too; and going up again from there gives the same milliseconds as the first time.
        /// </summary>
        [Test]
        public async Task Migrations_GoBackToTheProvidersTextAndForwardAgainUnchanged()
        {
            var times = new List<DateTime>
            {
                Start,
                Start.AddTicks(5_000_000),
                Start.AddTicks(1),
                Start.AddTicks(1_230_000),
                new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_995_000),
            };
            var random = new Random(4321);
            for (var i = 0; i < 200; i++)
            {
                times.Add(new DateTime(random.NextInt64(DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc));
            }

            await SeedAtTextTimesAsync(times);

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.Database.MigrateAsync();
                await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            }

            Assert.Multiple(async () =>
            {
                Assert.That(
                    await TextTimesAsync(),
                    Is.EqualTo(times.Select(t => DateTime.UnixEpoch.AddMilliseconds(UnixMillisecondsRoundedDown(t))
                        .ToString(ProviderTextFormat, CultureInfo.InvariantCulture))));
                Assert.That(await ScalarAsync("SELECT COUNT(*) FROM PingAttempts WHERE typeof(TimestampUtc) <> 'text';"), Is.Zero);
            });

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.Database.MigrateAsync();
            }

            Assert.That(await StoredValuesAsync(), Is.EqualTo(times.Select(UnixMillisecondsRoundedDown)));
        }

        /// <summary>
        /// The schema level goes up with the conversion of the times, which is what 1.14.0 and
        /// earlier cannot read, and back down with it.
        /// </summary>
        [Test]
        public async Task Migrations_RaiseTheSchemaLevelWithTheTimesConversionAndLowerItWithItsUndoing()
        {
            await using var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.Zero);

            await HostPingerDatabase.InitializeAsync(db);
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(HostPingerDatabase.SchemaLevel));

            await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.Zero);
        }

        /// <summary>
        /// A database a newer build upgraded past this one's level is refused before anything is
        /// written to it: not migrated, and its level left where the newer build put it, so the newer
        /// build installed again finds it as it was.
        /// </summary>
        [Test]
        public async Task InitializeAsync_RefusesADatabaseAboveItsSchemaLevelBeforeWritingToIt()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            }

            await ExecuteAsync($"PRAGMA user_version = {HostPingerDatabase.SchemaLevel + 1};");

            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                var refused = await Assert.ThrowsAsync<NewerDatabaseException>(() => HostPingerDatabase.InitializeAsync(db));
                Assert.That(refused!.DatabaseLevel, Is.EqualTo(HostPingerDatabase.SchemaLevel + 1));
            }

            await using (var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                Assert.Multiple(async () =>
                {
                    Assert.That(await check.Database.GetPendingMigrationsAsync(), Is.Not.Empty);
                    Assert.That(await ScalarAsync("PRAGMA user_version;"), Is.EqualTo(HostPingerDatabase.SchemaLevel + 1));
                });
            }
        }

        /// <summary>
        /// A database whose last migrator finished, as every one does but a killed one, has nothing
        /// to clear, and says nothing about it: the lock's table is there, but empty.
        /// </summary>
        [Test]
        public async Task InitializeAsync_ClearsNothingAfterAMigrationThatFinished()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastIn1_12);
            }

            var log = new DatabaseLog();
            await using var reopened = new HostPingerDbContext(log.Options(_databasePath));

            Assert.Multiple(async () =>
            {
                Assert.That(await HostPingerDatabase.InitializeAsync(reopened), Is.False);
                Assert.That(log.Entries, Is.Empty);
            });
        }

        /// <summary>
        /// Opening a database an older build left: it is migrated before anything reads it, which
        /// the settings could not be read without, since the table they live in came later. The
        /// summary is loaded too, so the first page has the hosts to draw.
        /// </summary>
        [Test]
        public async Task OpenAsync_MigratesADatabaseAnOlderBuildLeftBeforeReadingIt()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(BeforeUserSettings);
                db.Hosts.Add(new MonitoredHost { Name = "alpha", Address = "alpha.example", CreatedUtc = Start });
                await db.SaveChangesAsync();
            }

            var (settings, summaries) = Services(new PingerOptions());
            await HostPingerDatabase.OpenAsync(Factory(), settings, summaries);

            await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            Assert.Multiple(async () =>
            {
                Assert.That(await check.Database.GetPendingMigrationsAsync(), Is.Empty);
                Assert.That(summaries.Current.Rows.Select(r => r.Host.Name), Is.EqualTo(new[] { "alpha" }));
            });
            summaries.Dispose();
        }

        /// <summary>
        /// The settings are in force before the summary is read: a host that has missed one ping is
        /// down at a stored retry count of none, where at the configured three it would still be
        /// retrying — which is what the first page would draw if the order were the other way round.
        /// </summary>
        [Test]
        public async Task OpenAsync_ReadsTheSummaryWithTheStoredRetryCount()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await HostPingerDatabase.InitializeAsync(db);
                db.UserSettings.Add(new UserSetting { Key = $"{PingerOptions.SectionName}:{nameof(PingerOptions.RetryAttempts)}", Value = "0" });
                var host = new MonitoredHost { Name = "flaky", Address = "flaky.example", CreatedUtc = Start };
                db.Hosts.Add(host);
                await db.SaveChangesAsync();
                db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = Start, RoundtripMs = null });
                await db.SaveChangesAsync();
            }

            var (settings, summaries) = Services(new PingerOptions { RetryAttempts = 3 });
            await HostPingerDatabase.OpenAsync(Factory(), settings, summaries);

            Assert.Multiple(() =>
            {
                Assert.That(settings.CurrentPinger.RetryAttempts, Is.Zero);
                Assert.That(summaries.Current.MissesToDown, Is.EqualTo(1));
                Assert.That(summaries.Current.Rows.Single().Status, Is.EqualTo(HostStatus.Down));
            });
            summaries.Dispose();
        }

        /// <summary>
        /// What Program calls: the same opening, from the services the application registered,
        /// including the factory <see cref="HostPingerDatabase.AddHostPingerDatabase"/> adds.
        /// </summary>
        [Test]
        public async Task OpenAsync_FromTheApplicationsServices_OpensTheDatabaseItRegistered()
        {
            var paths = PingerPaths.Resolve(_databasePath, _directory);
            var services = new ServiceCollection()
                .AddHostPingerDatabase(paths)
                .AddLogging()
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<PingerOptions>>(new TestOptionsMonitor<PingerOptions>(new PingerOptions()))
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SecurityOptions>>(new TestOptionsMonitor<SecurityOptions>(new SecurityOptions { PasswordHash = "configured" }))
                .AddSingleton<UserSettingsStore>()
                .AddSingleton<HostSummaryCache>();
            await using var provider = services.BuildServiceProvider();

            await HostPingerDatabase.OpenAsync(provider);

            Assert.Multiple(async () =>
            {
                Assert.That(File.Exists(_databasePath), Is.True);
                await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
                Assert.That(await check.Database.GetPendingMigrationsAsync(), Is.Empty);
                Assert.That(provider.GetRequiredService<UserSettingsStore>().PasswordHash, Is.EqualTo("configured"));
                Assert.That(provider.GetRequiredService<HostSummaryCache>().Current.Rows, Is.Empty);
            });
        }

        /// <summary>
        /// Opened this way, without DatabaseStartup, as the benchmarks open it, the host summary still
        /// polls, so a refresh, which its loop answers and every host change waits for, comes back.
        /// It used to wait for the start to say the database was open, which only DatabaseStartup
        /// says, and a host delete in the benchmarks waited on it until the job was cancelled.
        /// </summary>
        [Test]
        public async Task OpenAsync_FromTheApplicationsServices_LeavesTheSummaryAnsweringRefreshes()
        {
            var paths = PingerPaths.Resolve(_databasePath, _directory);
            var services = new ServiceCollection()
                .AddHostPingerDatabase(paths)
                .AddLogging()
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<PingerOptions>>(new TestOptionsMonitor<PingerOptions>(new PingerOptions()))
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SecurityOptions>>(new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()))
                .AddSingleton<UserSettingsStore>()
                .AddSingleton<HostSummaryCache>();
            await using var provider = services.BuildServiceProvider();
            await HostPingerDatabase.OpenAsync(provider);

            var summaries = provider.GetRequiredService<HostSummaryCache>();
            await summaries.StartAsync(CancellationToken.None);
            try
            {
                Assert.That(
                    async () => await summaries.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10)),
                    Throws.Nothing,
                    "the summary's loop never started");
                Assert.That(provider.GetRequiredService<DatabaseOpening>().IsOpen, Is.False, "only the start says it is open");
            }
            finally
            {
                await summaries.StopAsync(CancellationToken.None);
            }
        }

        /// <summary>
        /// The stores, the backup and the pages have to share one lockout, or a backup or a restore
        /// would keep nothing from being changed. Each takes it as something it can do without, so
        /// nothing fails to build when it is missing: this is what says the application's services,
        /// built the way the application builds them, are handed the same one.
        /// </summary>
        [Test]
        public async Task AddHostPingerDatabase_HandsTheStoresAndTheBackupOneLockout()
        {
            var paths = PingerPaths.Resolve(_databasePath, _directory);
            var services = new ServiceCollection()
                .AddHostPingerDatabase(paths)
                .AddLogging()
                .AddSingleton(paths)
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<PingerOptions>>(new TestOptionsMonitor<PingerOptions>(new PingerOptions()))
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SecurityOptions>>(new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()))
                .AddSingleton<UserSettingsStore>()
                .AddSingleton<MaintenanceGate>()
                .AddSingleton<HostSummaryCache>()
                .AddSingleton<HostStore>()
                .AddSingleton<DatabaseSizeLimit>()
                .AddSingleton<DatabaseBackup>();
            await using var provider = services.BuildServiceProvider();
            await HostPingerDatabase.OpenAsync(provider);

            using var hold = (await provider.GetRequiredService<ChangeLockout>().TakeAsync(LockoutActivity.Restore)).Hold!;

            var host = await Assert.ThrowsAsync<ChangeRefusedException>(
                () => provider.GetRequiredService<HostStore>().AddAsync("alpha", "alpha.example", isEnabled: true));
            var password = await Assert.ThrowsAsync<ChangeRefusedException>(
                () => provider.GetRequiredService<UserSettingsStore>().SavePasswordHashAsync("a-hash"));
            var limit = await provider.GetRequiredService<DatabaseSizeLimit>().SaveAsync(1);
            var backup = await Assert.ThrowsAsync<IOException>(() => provider.GetRequiredService<DatabaseBackup>().CreateSnapshotAsync());

            Assert.Multiple(() =>
            {
                const string Refused = "A restore is under way. Nothing can be changed until it has finished.";
                Assert.That(new[] { host!.Message, password!.Message, limit }, Is.All.EqualTo(Refused));
                Assert.That(backup!.Message, Is.EqualTo("A restore is under way. Try again once it has finished."));
            });
        }

        /// <summary>
        /// The service's own start, which is where a lock is found after a start that was killed:
        /// its warning reaches the application's logging, which nothing handed the migrations.
        /// </summary>
        [Test]
        public async Task OpenAsync_LogsALockItClearsThroughTheApplicationsLogging()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastIn1_12);
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-10-22 07:03:51.8874913+00:00');");
            }

            var log = new DatabaseLog();
            var services = new ServiceCollection()
                .AddHostPingerDatabase(PingerPaths.Resolve(_databasePath, _directory))
                .AddLogging(logging => logging.AddProvider(log))
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<PingerOptions>>(new TestOptionsMonitor<PingerOptions>(new PingerOptions()))
                .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SecurityOptions>>(new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()))
                .AddSingleton<UserSettingsStore>()
                .AddSingleton<HostSummaryCache>();
            await using var provider = services.BuildServiceProvider();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await HostPingerDatabase.OpenAsync(provider, timeout.Token);

            Assert.That(log.Entries.Select(e => e.Level), Is.EqualTo(new[] { LogLevel.Warning }));
        }

        /// <summary>
        /// A database at <see cref="LastWithTextTimes"/> holding one host and an attempt at each of
        /// <paramref name="times"/>, Ids from 1 in that order, written as the provider wrote them
        /// then; and the table's AUTOINCREMENT counter past them all, as a delete of the newest
        /// attempts leaves it.
        /// </summary>
        private async Task SeedAtTextTimesAsync(IReadOnlyList<DateTime> times)
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            }

            await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
            await connection.OpenAsync();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Hosts (Id, Name, Address, IsEnabled, CreatedUtc) VALUES (1, 'alpha', 'alpha.example', 1, '2026-10-01 00:00:00');";
            await command.ExecuteNonQueryAsync();

            command.CommandText = "INSERT INTO PingAttempts (Id, HostId, TimestampUtc, RoundtripMs) VALUES ($id, 1, $time, 10);";
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            var time = command.Parameters.Add("$time", SqliteType.Text);
            for (var i = 0; i < times.Count; i++)
            {
                id.Value = i + 1;
                time.Value = times[i].ToString(ProviderTextFormat, CultureInfo.InvariantCulture);
                await command.ExecuteNonQueryAsync();
            }

            command.Parameters.Clear();
            command.CommandText = "UPDATE sqlite_sequence SET seq = 5000 WHERE name = 'PingAttempts';";
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        /// <summary>
        /// The Unix millisecond a time falls in, worked out apart from the code under test: in
        /// decimal, which holds a DateTime's ticks exactly, and rounded down, before 1970 too.
        /// </summary>
        private static long UnixMillisecondsRoundedDown(DateTime time) =>
            (long)Math.Floor((decimal)(time.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond);

        /// <summary>The attempts' times as the column holds them, in Id order.</summary>
        private async Task<List<long>> StoredValuesAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TimestampUtc FROM PingAttempts ORDER BY Id;";
            await using var reader = await command.ExecuteReaderAsync();
            var values = new List<long>();
            while (await reader.ReadAsync())
            {
                values.Add(reader.GetInt64(0));
            }

            return values;
        }

        private async Task<List<string>> TextTimesAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TimestampUtc FROM PingAttempts ORDER BY Id;";
            await using var reader = await command.ExecuteReaderAsync();
            var texts = new List<string>();
            while (await reader.ReadAsync())
            {
                texts.Add(reader.GetString(0));
            }

            return texts;
        }

        private async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task<string?> JournalModeAsync()
        {
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return (string?)await command.ExecuteScalarAsync();
        }

        private static async Task<long> JournalSizeLimitAsync(HostPingerDbContext db)
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_size_limit;";
                return Convert.ToInt64(await command.ExecuteScalarAsync());
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        private TestDb.Factory Factory() => new(HostPingerDatabase.Options(_databasePath));

        /// <summary>
        /// What is logged under HostPingerDatabase's own category, through whichever logger factory
        /// it is added to; EF's own lines, under categories of their own, are left out.
        /// </summary>
        private sealed class DatabaseLog : ILoggerProvider
        {
            private readonly TestLogger<DatabaseLog> _logger = new();

            public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _logger.Entries;

            /// <summary>Options for a context whose own logging comes here, as a test's context has none otherwise.</summary>
            public DbContextOptions<HostPingerDbContext> Options(string databasePath) =>
                new DbContextOptionsBuilder<HostPingerDbContext>()
                    .UseSqlite(HostPingerDatabase.ConnectionString(databasePath))
                    .UseLoggerFactory(LoggerFactory.Create(logging => logging.AddProvider(this)))
                    .Options;

            public ILogger CreateLogger(string categoryName) =>
                categoryName == typeof(HostPingerDatabase).FullName ? _logger : NullLogger.Instance;

            public void Dispose()
            {
            }
        }

        private (UserSettingsStore Settings, HostSummaryCache Summaries) Services(PingerOptions defaults)
        {
            var settings = new UserSettingsStore(
                Factory(),
                new TestOptionsMonitor<PingerOptions>(defaults),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
            return (settings, new HostSummaryCache(Factory(), settings, NullLogger<HostSummaryCache>.Instance));
        }
    }
}
