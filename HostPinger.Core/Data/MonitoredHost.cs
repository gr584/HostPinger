namespace HostPinger.Core.Data
{
    /// <summary>A host being monitored by the pinger.</summary>
    public class MonitoredHost
    {
        /// <summary>
        /// The longest name a host can have. SQLite holds a TEXT column to no length, so this is a
        /// bound only because <see cref="Services.HostStore"/> and the Hosts page's form keep to it.
        /// </summary>
        public const int MaxNameLength = 100;

        /// <summary>The longest address a host can have: the longest name DNS allows.</summary>
        public const int MaxAddressLength = 253;

        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Host name or IP address to ping.</summary>
        public string Address { get; set; } = string.Empty;

        public bool IsEnabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; }

        public List<PingAttempt> PingAttempts { get; set; } = [];
    }
}
