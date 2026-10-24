using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
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
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await HostPingerDatabase.InitializeAsync(db, timeout.Token);
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

        private TestDb.Factory Factory() => new(HostPingerDatabase.Options(_databasePath));

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
