namespace HostPinger.Core.Data
{
    /// <summary>A single recorded ping of a host.</summary>
    public class PingAttempt
    {
        public long Id { get; set; }

        public int HostId { get; set; }

        public MonitoredHost? Host { get; set; }

        public DateTime TimestampUtc { get; set; }

        /// <summary>Round-trip time in milliseconds, or null if the host did not respond.</summary>
        public int? RoundtripMs { get; set; }
    }
}
