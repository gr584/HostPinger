namespace HostPinger.Core.Options
{
    /// <summary>
    /// Absolute file paths resolved once at startup from configuration. This type owns the
    /// defaults for both paths, so an unset key means the same thing everywhere;
    /// <see cref="PingerOptions"/> carries the configured values verbatim and never substitutes
    /// its own.
    /// </summary>
    /// <param name="DatabasePath">The SQLite database file.</param>
    /// <param name="SettingsPath">
    /// JSON overlay holding the settings edited on the Configuration page. It is layered on top of
    /// appsettings.json, so it only holds the keys the user has changed.
    /// </param>
    public sealed record PingerPaths(string DatabasePath, string SettingsPath)
    {
        /// <summary>Used when <see cref="PingerOptions.DatabasePath"/> is not configured.</summary>
        public const string DatabaseFileName = "hostpinger.db";

        /// <summary>Used when <see cref="PingerOptions.UserSettingsPath"/> is not configured.</summary>
        public const string SettingsFileName = "usersettings.json";

        public static PingerPaths Resolve(
            string? configuredDatabasePath,
            string? configuredSettingsPath,
            string contentRootPath)
        {
            var databasePath = Expand(
                string.IsNullOrWhiteSpace(configuredDatabasePath) ? DatabaseFileName : configuredDatabasePath,
                contentRootPath);

            // An unset overlay path falls back to the database directory, which is where the
            // overlay lived before it became configurable — an appsettings.json predating the key
            // keeps working, and that directory is writable for a Windows service where the
            // install folder is not.
            var settingsPath = string.IsNullOrWhiteSpace(configuredSettingsPath)
                ? Path.Combine(Path.GetDirectoryName(databasePath)!, SettingsFileName)
                : Expand(configuredSettingsPath, contentRootPath);

            return new PingerPaths(databasePath, settingsPath);
        }

        /// <summary>
        /// Expands environment variables (e.g. %ProgramData%) and anchors a relative path to the
        /// content root.
        /// </summary>
        private static string Expand(string configuredPath, string contentRootPath) =>
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath), contentRootPath);
    }
}
