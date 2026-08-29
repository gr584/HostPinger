namespace HostPinger.Core.Options
{
    /// <summary>Settings bound from the "Pinger" section of appsettings.json.</summary>
    public class PingerOptions
    {
        public const string SectionName = "Pinger";

        /// <summary>
        /// Path to the SQLite database file, or null when the key is absent. Read once at startup
        /// by <see cref="PingerPaths.Resolve"/>, which expands it and applies the default.
        /// </summary>
        public string? DatabasePath { get; set; }

        /// <summary>How often every enabled host is pinged.</summary>
        public int IntervalSeconds { get; set; } = 30;

        /// <summary>How long to wait for a reply before recording the host as down.</summary>
        public int TimeoutSeconds { get; set; } = 5;

        /// <summary>
        /// How long to wait for a host name to become an IP address. Bounded separately from
        /// <see cref="TimeoutSeconds"/>, which only covers the wait for a reply and so leaves
        /// resolution unbounded, and because the outcome differs: a host that does not answer is
        /// recorded as down, while a name that does not resolve is skipped for that round.
        /// </summary>
        public int ResolveTimeoutSeconds { get; set; } = 3;

        /// <summary>
        /// How many times a host that misses a ping is retried before it counts as down. It is
        /// retrying while it has those attempts left, so a single dropped packet does not read as
        /// an outage. Zero allows no retries and makes the first missed ping count, which is what
        /// the application did before this setting existed. Negative values are read as zero.
        /// </summary>
        public int RetryAttempts { get; set; } = 3;

        /// <summary>
        /// Maximum database file size. The oldest ping attempts are pruned when the file grows
        /// beyond this; zero or negative disables pruning.
        /// </summary>
        public int MaxDatabaseSizeMb { get; set; } = 1024;

        public long MaxDatabaseSizeBytes => (long)MaxDatabaseSizeMb * 1024 * 1024;
    }
}
