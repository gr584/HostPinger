namespace HostPinger.Core.Options
{
    /// <summary>Absolute file paths resolved once at startup from configuration.</summary>
    /// <param name="DatabasePath">The SQLite database file.</param>
    /// <param name="SettingsPath">
    /// JSON overlay holding the settings edited on the Configuration page. It sits next to the
    /// database — that directory is writable for a Windows service, unlike the install folder —
    /// and is layered on top of appsettings.json, so it only holds the keys the user has changed.
    /// </param>
    public sealed record PingerPaths(string DatabasePath, string SettingsPath)
    {
        public const string SettingsFileName = "usersettings.json";

        public static PingerPaths Resolve(string? configuredDatabasePath, string contentRootPath)
        {
            var databasePath = PingerOptions.ResolveDatabasePath(configuredDatabasePath, contentRootPath);
            var directory = Path.GetDirectoryName(databasePath)!;
            return new PingerPaths(databasePath, Path.Combine(directory, SettingsFileName));
        }
    }
}
