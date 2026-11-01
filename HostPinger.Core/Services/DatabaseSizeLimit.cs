using HostPinger.Core.Options;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Keeps the database's size limit to what its disk can safely hold: at most half of the room
    /// the database has there, its own files and the free space beside them, less
    /// <see cref="DatabaseBackup.FreeSpaceMarginBytes"/>. A backup writes a copy of the whole
    /// database beside it, and a restore keeps the old database there until its upload is in
    /// place, so a database allowed more than half of its room could no longer be backed up, and
    /// one at its limit leaves an upgrade's migration the other half to work in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Half of the room the database has rather than of the disk: it usually shares the disk with
    /// the operating system and everything else, and none of that is room it can have. A limit is
    /// checked when it is saved and when a restore brings one, and a limit of zero, which used to
    /// turn pruning off, is replaced as the service starts.
    /// </para>
    /// <para>
    /// Nothing here prunes because the room shrinks afterwards, when something else fills the
    /// disk; the page says so instead. Pruning to keep up with the room would hand whatever was
    /// filling the disk the history's space a slice at a time, until there was no history left.
    /// </para>
    /// </remarks>
    public sealed class DatabaseSizeLimit(PingerPaths paths, UserSettingsStore settings, ILogger<DatabaseSizeLimit> logger)
    {
        private const long BytesPerMb = 1024 * 1024;

        /// <summary>
        /// What the disk beside the database has free. Settable for the tests, which cannot fill a
        /// disk to see a limit refused.
        /// </summary>
        internal Func<long> MeasureFreeBytes { get; init; } =
            () => new DriveInfo(Path.GetDirectoryName(paths.DatabasePath)!).AvailableFreeSpace;

        /// <summary>The largest limit that is safe for the database on its disk as it is now, in MB.</summary>
        public int SafeLimitMb() => SafeLimitMbFor(FootprintBytes(paths.DatabasePath) + MeasureFreeBytes());

        /// <summary>
        /// Saves <paramref name="limitMb"/> as the size limit, or says why not: it is under 1 MB, or
        /// more than <see cref="SafeLimitMb"/>.
        /// </summary>
        /// <returns>Null once the limit is saved, and otherwise the sentence to show whoever asked.</returns>
        public async Task<string?> SaveAsync(int limitMb, CancellationToken cancellationToken = default)
        {
            if (limitMb < 1)
            {
                return "Enter a size of at least 1 MB.";
            }

            var safe = SafeLimitMb();
            if (limitMb > safe)
            {
                return $"This disk can safely hold {safe:N0} MB at most: half of what the database's files and the "
                    + "free space beside them come to, so that a backup's copy always fits.";
            }

            await settings.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = limitMb }, cancellationToken);
            return null;
        }

        /// <summary>
        /// Gives a limit of zero or less, which used to turn pruning off, a real one as the service
        /// starts: the safe limit, or the database's own size where that is more, so that starting
        /// a new version deletes nothing that is there. From then on the oldest history goes as
        /// new history arrives, as under any limit. A warning in the log says what it was set to.
        /// </summary>
        public async Task SettleAsync(CancellationToken cancellationToken = default)
        {
            var limitMb = settings.CurrentPinger.MaxDatabaseSizeMb;
            if (limitMb > 0)
            {
                return;
            }

            var footprint = FootprintBytes(paths.DatabasePath);
            var settled = Math.Max(
                SafeLimitMbFor(footprint + MeasureFreeBytes()),
                (int)Math.Min((footprint + BytesPerMb - 1) / BytesPerMb, int.MaxValue));
            await settings.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = settled }, cancellationToken);
            logger.LogWarning(
                "The database size limit was {Limit} MB, which no longer turns pruning off. It is {Settled} MB now: "
                + "the most this disk can safely hold, or the database's size where that is more. "
                + "Change it on the Configuration page.",
                limitMb,
                settled);
        }

        /// <summary>
        /// The safe limit for a database with <paramref name="roomBytes"/> to itself, its own files
        /// and the free space beside them: half, less the margin, in whole MB, and never under 1 MB,
        /// since a limit of nothing is not one.
        /// </summary>
        internal static int SafeLimitMbFor(long roomBytes) =>
            (int)Math.Clamp((roomBytes - DatabaseBackup.FreeSpaceMarginBytes) / 2 / BytesPerMb, 1, int.MaxValue);

        /// <summary>
        /// What the database at <paramref name="databasePath"/> takes on disk: the file, and the
        /// -wal and -shm SQLite keeps beside it in WAL mode.
        /// </summary>
        internal static long FootprintBytes(string databasePath)
        {
            long bytes = 0;
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = new FileInfo(databasePath + suffix);
                if (file.Exists)
                {
                    bytes += file.Length;
                }
            }

            return bytes;
        }
    }
}
