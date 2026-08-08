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

        /// <summary>
        /// Path to the JSON overlay holding the runtime-editable settings, resolved like
        /// <see cref="DatabasePath"/>. Null means "next to the database".
        /// </summary>
        /// <remarks>
        /// Read before the overlay is registered as a configuration source, so it has to come from
        /// appsettings.json: setting it inside the overlay would be asking the file to say where it
        /// lives, and is ignored. It is configurable at all so the overlay can sit outside the
        /// install folder, which a Windows service cannot write to and which an MSI upgrade
        /// replaces wholesale.
        /// </remarks>
        public string? UserSettingsPath { get; set; }

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
        /// Maximum database file size. The oldest ping attempts are pruned when the file grows
        /// beyond this; zero or negative disables pruning.
        /// </summary>
        public int MaxDatabaseSizeMb { get; set; } = 100;

        public long MaxDatabaseSizeBytes => (long)MaxDatabaseSizeMb * 1024 * 1024;
    }
}
