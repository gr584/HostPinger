namespace HostPinger.Core.Services
{
    public interface IPingSender
    {
        /// <summary>
        /// Pings the address once. Returns the round-trip time in milliseconds, or null if the
        /// host is down or unreachable.
        /// </summary>
        Task<int?> SendPingAsync(string address, int timeoutMilliseconds, CancellationToken cancellationToken = default);
    }
}
