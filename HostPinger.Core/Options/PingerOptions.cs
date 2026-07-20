namespace HostPinger.Core.Options
{
    /// <summary>Settings bound from the "Pinger" section of appsettings.json.</summary>
    public class PingerOptions
    {
        public const string SectionName = "Pinger";

        /// <summary>
        /// Path to the SQLite database file. Environment variables (e.g. %ProgramData%) are
        /// expanded; relative paths are resolved against the application content root.
        /// </summary>
        public string DatabasePath { get; set; } = "hostpinger.db";

        /// <summary>How often every enabled host is pinged.</summary>
        public int IntervalSeconds { get; set; } = 30;

        /// <summary>How long to wait for a reply before recording the host as down.</summary>
        public int TimeoutMilliseconds { get; set; } = 5000;

        /// <summary>
        /// Maximum database file size. The oldest ping attempts are pruned when the file grows
        /// beyond this; zero or negative disables pruning.
        /// </summary>
        public int MaxDatabaseSizeMb { get; set; } = 100;

        public long MaxDatabaseSizeBytes => (long)MaxDatabaseSizeMb * 1024 * 1024;

        public static string ResolveDatabasePath(string? configuredPath, string basePath)
        {
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? "hostpinger.db"
                : Environment.ExpandEnvironmentVariables(configuredPath);
            return Path.GetFullPath(path, basePath);
        }
    }
}
