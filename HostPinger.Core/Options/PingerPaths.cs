namespace HostPinger.Core.Options
{
    /// <summary>
    /// The absolute database path resolved once at startup from configuration. This type owns the
    /// default, so an unset key means the same thing everywhere; <see cref="PingerOptions"/>
    /// carries the configured value verbatim and never substitutes its own.
    /// </summary>
    /// <param name="DatabasePath">The SQLite database file.</param>
    public sealed record PingerPaths(string DatabasePath)
    {
        /// <summary>Used when <see cref="PingerOptions.DatabasePath"/> is not configured.</summary>
        public const string DatabaseFileName = "hostpinger.db";

        /// <summary>
        /// Where the database lives when nothing is configured. The default is per-OS rather than
        /// a path in appsettings.json because the same file ships to both platforms: %ProgramData%
        /// on Windows, which is where the installed service has always kept it, and
        /// /var/lib/hostpinger elsewhere, which is the conventional home for service state and the
        /// directory the container image mounts its volume on.
        /// </summary>
        /// <remarks>
        /// CommonApplicationData is only consulted on Windows. .NET maps it to /usr/share on Unix,
        /// which belongs to the package manager and is not writable by a service account.
        /// </remarks>
        public static string DefaultDataDirectory => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HostPinger")
            : "/var/lib/hostpinger";

        public static PingerPaths Resolve(string? configuredDatabasePath, string contentRootPath)
        {
            var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
                ? Path.GetFullPath(Path.Combine(DefaultDataDirectory, DatabaseFileName))
                : Expand(configuredDatabasePath, contentRootPath);

            return new PingerPaths(databasePath);
        }

        /// <summary>
        /// Expands environment variables (e.g. %ProgramData%) and anchors a relative path to the
        /// content root.
        /// </summary>
        private static string Expand(string configuredPath, string contentRootPath) =>
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath), contentRootPath);
    }
}
