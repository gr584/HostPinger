namespace HostPinger.Core.Options
{
    /// <summary>
    /// The settings that can be changed while the application is running. Properties left null
    /// are not written, so each group on the Configuration page can save without disturbing the
    /// settings owned by the others.
    /// </summary>
    public sealed record PingerSettingsUpdate
    {
        public int? IntervalSeconds { get; init; }

        public int? TimeoutSeconds { get; init; }

        public int? ResolveTimeoutSeconds { get; init; }

        public int? RetryAttempts { get; init; }

        public int? MaxDatabaseSizeMb { get; init; }
    }
}
