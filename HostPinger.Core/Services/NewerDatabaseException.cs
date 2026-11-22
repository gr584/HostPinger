namespace HostPinger.Core.Services
{
    /// <summary>
    /// The database was upgraded by a newer HostPinger, to a schema level this build cannot read; see
    /// <see cref="HostPingerDatabase.SchemaLevel"/>. Thrown before anything is written to it, so the
    /// newer build installed again finds the database as it left it.
    /// </summary>
    public sealed class NewerDatabaseException(long databaseLevel, long buildLevel) : InvalidOperationException(
        $"The database was upgraded by a newer version of HostPinger (schema level {databaseLevel}; this version reads up to "
        + $"{buildLevel}), and going back to an earlier version is not supported. Install the newer version again. "
        + "To go back regardless, stop the service and put a backup file made before the upgrade in place of the "
        + "database file, which loses the history recorded since; a restore from the Configuration page would "
        + "upgrade the backup again.")
    {
        /// <summary>The level the database is at.</summary>
        public long DatabaseLevel { get; } = databaseLevel;

        /// <summary>The highest level this build reads.</summary>
        public long BuildLevel { get; } = buildLevel;
    }
}
