namespace HostPinger.Core.Services
{
    /// <summary>
    /// A migration is pending and the disk beside the database has too little free for it, for a
    /// command that migrates the database itself rather than waiting for room as the service does
    /// (<see cref="DatabaseStartup"/>). Thrown before anything is written.
    /// </summary>
    public sealed class MigrationRoomException(long neededBytes, long freeBytes) : IOException(
        $"The database needs upgrading first, which needs about {neededBytes >> 20} MB free beside it, and the disk has "
        + $"{freeBytes >> 20} MB. Nothing was changed. Free some room and run this again.")
    {
        /// <summary>What the migration needs free, margin included.</summary>
        public long NeededBytes { get; } = neededBytes;

        /// <summary>What the disk had free.</summary>
        public long FreeBytes { get; } = freeBytes;
    }
}
