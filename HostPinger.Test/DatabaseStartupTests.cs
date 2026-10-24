using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HostPinger.Test
{
    /// <summary>
    /// Opening the database as a step of the host's start rather than ahead of it, which is what
    /// lets a Windows service tell the service manager it is running before an upgrade's
    /// migrations begin. What has to hold either way is that nothing else starts first.
    /// </summary>
    public class DatabaseStartupTests
    {
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
        /// A service registered ahead of it finds the database migrated and an earlier run's temp
        /// file gone when it starts, as the web server and the ping rounds do: the host runs every
        /// StartingAsync before any StartAsync, whatever order the services were added in.
        /// </summary>
        [Test]
        public async Task Host_OpensTheDatabaseBeforeItStartsAnyService()
        {
            var paths = PingerPaths.Resolve(_databasePath, _directory);
            var leftover = Path.Combine(_directory, "hostpinger-backup-left-by-a-crash.tmp");
            await File.WriteAllTextAsync(leftover, "a download that died with the process");
            var probe = new StartProbe(_databasePath, leftover);

            using var host = new HostBuilder()
                .ConfigureServices(services => services
                    .AddSingleton(paths)
                    .AddHostPingerDatabase(paths)
                    .AddSingleton<IOptionsMonitor<PingerOptions>>(new TestOptionsMonitor<PingerOptions>(new PingerOptions()))
                    .AddSingleton<IOptionsMonitor<SecurityOptions>>(new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()))
                    .AddSingleton<UserSettingsStore>()
                    .AddSingleton<HostSummaryCache>()
                    .AddSingleton(probe)
                    .AddHostedService(services => services.GetRequiredService<StartProbe>())
                    .AddHostedService<DatabaseStartup>())
                .Build();

            await host.StartAsync();
            await host.StopAsync();

            Assert.Multiple(() =>
            {
                Assert.That(probe.Started, Is.True);
                Assert.That(probe.PendingMigrations, Is.Empty, "migrated before the first service started");
                Assert.That(probe.LeftoverStillThere, Is.False, "swept before the first service started");
            });
        }

        /// <summary>
        /// Records what a service starting first would find. Registered ahead of DatabaseStartup,
        /// so that only the host's ordering of StartingAsync before StartAsync can make it pass.
        /// </summary>
        private sealed class StartProbe(string databasePath, string leftover) : IHostedService
        {
            public bool Started { get; private set; }

            public List<string> PendingMigrations { get; private set; } = ["not read"];

            public bool LeftoverStillThere { get; private set; } = true;

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                Started = true;
                LeftoverStillThere = File.Exists(leftover);
                await using var db = new HostPingerDbContext(HostPingerDatabase.Options(databasePath));
                PendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
