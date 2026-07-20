namespace HostPinger.Core.Data
{
    /// <summary>A host being monitored by the pinger.</summary>
    public class MonitoredHost
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>Host name or IP address to ping.</summary>
        public string Address { get; set; } = string.Empty;

        public bool IsEnabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; }

        public List<PingAttempt> PingAttempts { get; set; } = [];
    }
}
