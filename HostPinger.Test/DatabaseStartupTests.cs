using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HostPinger.Test
{
    /// <summary>
    /// Opening the database as the host starts, alongside the rest of the host rather than ahead of
    /// it. What has to hold is that nothing that uses the database goes on before it is open, that
    /// a migration waits for the room it needs, and that a database from a newer build is refused.
    /// </summary>
    public class DatabaseStartupTests
    {
        /// <summary>The last migration with the attempts' times stored as text, 1.13.0's and 1.14.0's.</summary>
        private const string LastWithTextTimes = "20261016020813_AddRoundtripToPartialPingIndexes";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private string _directory = null!;
        private string _databasePath = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-startup-{Guid.NewGuid():N}");
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
        /// Once the start says the database is open, it is migrated, an earlier run's temp file is
        /// gone, and its schema level is known for the banner.
        /// </summary>
        [Test]
        public async Task Host_OpensTheDatabaseInTheBackground()
        {
            var leftover = Path.Combine(_directory, "hostpinger-backup-left-by-a-crash.tmp");
            await File.WriteAllTextAsync(leftover, "a download that died with the process");

            using var host = BuildHost(new PingerOptions());
            await host.StartAsync();
            var opening = host.Services.GetRequiredService<DatabaseOpening>();
            await opening.Opened.WaitAsync(Patience);
            await host.StopAsync();

            await using var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            Assert.Multiple(async () =>
            {
                Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
                Assert.That(File.Exists(leftover), Is.False);
                Assert.That(opening.SchemaLevel, Is.EqualTo(HostPingerDatabase.SchemaLevel));
                Assert.That(opening.RollbackUnsupported, Is.True);
                Assert.That(opening.UpgradeStartedUtc, Is.Null, "a new database is created, not upgraded");
            });
        }

        /// <summary>
        /// A size limit of zero, which used to turn pruning off, is given a real one before the
        /// database counts as open, so the first ping round already prunes against it, and it is
        /// stored, so it stays that way after the next start too.
        /// </summary>
        [Test]
        public async Task Host_GivesALimitOfZeroARealOneBeforeTheDatabaseIsOpen()
        {
            using var host = BuildHost(new PingerOptions { MaxDatabaseSizeMb = 0 });
            await host.StartAsync();
            await host.Services.GetRequiredService<DatabaseOpening>().Opened.WaitAsync(Patience);
            var limitWhenOpen = host.Services.GetRequiredService<UserSettingsStore>().CurrentPinger.MaxDatabaseSizeMb;
            await host.StopAsync();

            await using var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            var stored = await db.UserSettings.SingleAsync(s => s.Key == "Pinger:MaxDatabaseSizeMb");
            Assert.Multiple(() =>
            {
                Assert.That(limitWhenOpen, Is.Positive);
                Assert.That(stored.Value, Is.EqualTo(limitWhenOpen.ToString()));
            });
        }

        /// <summary>
        /// An upgrade waits, without touching the file, while the disk has less free than the
        /// migration needs, says how much it needs and has, and goes ahead at the next measurement
        /// after there is room.
        /// </summary>
        [Test]
        public async Task Host_WaitsForTheRoomAnUpgradeNeedsAndUpgradesOnceThereIs()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            }

            var fileBytes = new FileInfo(_databasePath).Length;
            var free = 1024L * 1024;

            using var host = BuildHost(new PingerOptions(), () => Interlocked.Read(ref free));
            await host.StartAsync();
            var opening = host.Services.GetRequiredService<DatabaseOpening>();
            await WaitForAsync(() => opening.Stage == DatabaseOpeningStage.WaitingForRoom);

            Assert.Multiple(async () =>
            {
                Assert.That(opening.RoomNeededBytes, Is.EqualTo(fileBytes / 2 + DatabaseBackup.FreeSpaceMarginBytes));
                Assert.That(opening.RoomFreeBytes, Is.EqualTo(1024L * 1024));
                Assert.That(opening.Opened.IsCompleted, Is.False);
                Assert.That(opening.IsUpgrading, Is.False, "upgrading without the room");
                Assert.That(await PendingMigrationsAsync(), Is.Not.Empty, "migrated without the room");
            });

            Interlocked.Exchange(ref free, long.MaxValue);
            await opening.Opened.WaitAsync(Patience);
            await host.StopAsync();

            Assert.Multiple(async () =>
            {
                Assert.That(await PendingMigrationsAsync(), Is.Empty);
                Assert.That(opening.UpgradeStartedUtc, Is.Not.Null, "an older database is upgraded");
                Assert.That(opening.UpgradeJournalBytes, Is.Not.Null, "the journal files were never measured");
                Assert.That(opening.IsUpgrading, Is.False, "still upgrading once open");
            });
        }

        /// <summary>
        /// A database a newer build upgraded is refused, and the host stays up to say why rather than
        /// failing into a restart that would refuse it again.
        /// </summary>
        [Test]
        public async Task Host_RefusesANewerDatabaseAndStaysUpToSaySo()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.Database.MigrateAsync();
            }

            await using (var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {HostPingerDatabase.SchemaLevel + 1};";
                await command.ExecuteNonQueryAsync();
            }

            using var host = BuildHost(new PingerOptions());
            await host.StartAsync();
            var opening = host.Services.GetRequiredService<DatabaseOpening>();
            await WaitForAsync(() => opening.Stage == DatabaseOpeningStage.Refused);
            var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;
            await host.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(opening.Refusal, Does.Contain("newer version"));
                Assert.That(opening.Opened.IsCompleted, Is.False);
                Assert.That(stopping, Is.False, "the host stopped");
            });
        }

        private IHost BuildHost(PingerOptions options, Func<long>? freeBytes = null)
        {
            var paths = PingerPaths.Resolve(_databasePath, _directory);
            return new HostBuilder()
                .ConfigureServices(services => services
                    .AddSingleton(paths)
                    .AddHostPingerDatabase(paths)
                    .AddSingleton<IOptionsMonitor<PingerOptions>>(new TestOptionsMonitor<PingerOptions>(options))
                    .AddSingleton<IOptionsMonitor<SecurityOptions>>(new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()))
                    .AddSingleton<UserSettingsStore>()
                    .AddSingleton<HostSummaryCache>()
                    .AddSingleton(provider => new DatabaseSizeLimit(
                        paths, provider.GetRequiredService<UserSettingsStore>(), NullLogger<DatabaseSizeLimit>.Instance)
                    {
                        MeasureFreeBytes = freeBytes ?? (() => long.MaxValue),
                    })
                    .AddHostedService(provider => new DatabaseStartup(
                        provider,
                        paths,
                        provider.GetRequiredService<DatabaseOpening>(),
                        provider.GetRequiredService<DatabaseSizeLimit>(),
                        provider.GetRequiredService<IHostApplicationLifetime>(),
                        NullLogger<DatabaseStartup>.Instance)
                    {
                        RoomRecheckInterval = TimeSpan.FromMilliseconds(50),
                    }))
                .Build();
        }

        private async Task<List<string>> PendingMigrationsAsync()
        {
            await using var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            return (await db.Database.GetPendingMigrationsAsync()).ToList();
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!condition())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "gave up waiting");
                await Task.Delay(25);
            }
        }
    }
}
