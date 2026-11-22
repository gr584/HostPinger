using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Opens the database as the host starts. First the sweep of temp files an earlier run left, so
    /// that what they took counts as room. Then, if a migration is pending and the disk has too
    /// little free for it, a wait for room, measured again every <see cref="RoomRecheckInterval"/>.
    /// Then <see cref="HostPingerDatabase.OpenAsync(IServiceProvider, CancellationToken)"/>, and a
    /// real size limit in place of a zero, which used to turn pruning off
    /// (<see cref="DatabaseSizeLimit.SettleAsync"/>). <see cref="DatabaseOpening"/> says how far it
    /// has got.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rest of the host does not wait for any of this. The web server comes up straight away,
    /// so that the pages can say what the start is waiting for: until the database is open, the
    /// layout draws only that, in place of the page, and the services that use the database (the
    /// ping rounds and the host summary's polling) wait for <see cref="DatabaseOpening.Opened"/>.
    /// </para>
    /// <para>
    /// A database from a newer HostPinger is refused and stays refused, with the web server still
    /// up to say so, rather than the service failing and being restarted into the same refusal.
    /// Anything else that stops the opening stops the host with a failing exit code, as a failed
    /// start did before, so that the service manager restarts it.
    /// </para>
    /// </remarks>
    public sealed class DatabaseStartup(
        IServiceProvider services,
        PingerPaths paths,
        DatabaseOpening opening,
        DatabaseSizeLimit sizeLimit,
        IHostApplicationLifetime lifetime,
        ILogger<DatabaseStartup> logger) : BackgroundService
    {
        /// <summary>How long a start waiting for room waits before measuring the disk again.</summary>
        public static readonly TimeSpan DefaultRoomRecheckInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// How long a start waiting for room waits before measuring the disk again. Settable for the
        /// tests, which cannot wait a minute.
        /// </summary>
        internal TimeSpan RoomRecheckInterval { get; init; } = DefaultRoomRecheckInterval;

        /// <summary>How often a migration's journal is measured for <see cref="DatabaseOpening.UpgradeJournalBytes"/>.</summary>
        internal TimeSpan UpgradeSampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // A download that died with the process or an upload cut off by a restart leaves its
                // temp file beside the database, with whatever SQLite was keeping beside that; nothing
                // holds them now, so they go.
                DatabaseBackup.DeleteLeftoverTempFiles(paths);

                var migrationBytes = await WaitForRoomAsync(stoppingToken);
                opening.Migrate(migrationBytes > 0, DateTime.UtcNow);

                // Migrations, then the settings, then the host summary; see OpenAsync for the order.
                using (var sampling = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    var sampler = migrationBytes > 0 ? SampleUpgradeAsync(sampling.Token) : Task.CompletedTask;
                    try
                    {
                        await HostPingerDatabase.OpenAsync(services, stoppingToken);
                    }
                    finally
                    {
                        await sampling.CancelAsync();
                        await sampler;
                    }
                }

                // Before the first round, which would otherwise go on not pruning.
                await sizeLimit.SettleAsync(stoppingToken);

                var dbFactory = services.GetRequiredService<IDbContextFactory<HostPingerDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                opening.Open(await HostPingerDatabase.ReadSchemaLevelOnItsOwnAsync(db, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The host stopped while the start waited or migrated. A migration stopped partway
                // rolls back, and the next start begins it again.
            }
            catch (NewerDatabaseException ex)
            {
                logger.LogError("{Refusal}", ex.Message);
                opening.Refuse(ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "The database could not be opened.");
                Environment.ExitCode = 1;
                lifetime.StopApplication();
            }
        }

        /// <summary>
        /// Measures the migration's journal every <see cref="UpgradeSampleInterval"/> until
        /// <paramref name="cancellationToken"/> says it is over, for the banner.
        /// </summary>
        private async Task SampleUpgradeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    opening.ReportUpgrade(JournalBytes());
                    await Task.Delay(UpgradeSampleInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private long JournalBytes() => SizeOf(paths.DatabasePath + "-wal") + SizeOf(paths.DatabasePath + "-journal");

        private static long SizeOf(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.Length : 0;
            }
            catch (IOException)
            {
                // Gone between the look and the measure, as a journal is at a commit.
                return 0;
            }
        }

        /// <summary>
        /// Returns once the disk has the room a pending migration needs, margin included, or at
        /// once when none is pending: the room the migration needs, the margin left out, or zero.
        /// </summary>
        private async Task<long> WaitForRoomAsync(CancellationToken stoppingToken)
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<HostPingerDbContext>>();
            var logged = false;
            while (true)
            {
                long migrationBytes;
                await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
                {
                    migrationBytes = await HostPingerDatabase.MigrationRoomBytesAsync(db, paths.DatabasePath, stoppingToken);
                }

                if (migrationBytes == 0)
                {
                    return 0;
                }

                var needed = migrationBytes + DatabaseBackup.FreeSpaceMarginBytes;
                var free = sizeLimit.MeasureFreeBytes();
                if (free >= needed)
                {
                    if (logged)
                    {
                        logger.LogInformation("The disk has the room the database's upgrade needs now; upgrading it.");
                    }

                    return migrationBytes;
                }

                opening.WaitForRoom(needed, free);
                if (!logged)
                {
                    logged = true;
                    logger.LogWarning(
                        "The database's upgrade needs about {NeededMb} MB free beside it, and the disk has {FreeMb} MB. "
                        + "Nothing is recorded until there is room, which is checked every {Minutes} minute(s).",
                        needed >> 20,
                        free >> 20,
                        RoomRecheckInterval.TotalMinutes);
                }

                await Task.Delay(RoomRecheckInterval, stoppingToken);
            }
        }
    }
}
