namespace HostPinger.Core.Services
{
    public interface IPingSender
    {
        /// <summary>
        /// Resolves the address and pings it once.
        /// </summary>
        /// <param name="address">Host name or IP literal.</param>
        /// <param name="timeoutMilliseconds">How long to wait for the echo reply.</param>
        /// <param name="resolveTimeoutMilliseconds">
        /// How long to wait for the name to become an IP. Bounded separately from the reply because
        /// the reply timeout does not cover resolution — and because the two fail differently: a
        /// silent host is a recorded outage, an unresolvable name is nothing to record at all.
        /// Ignored for an IP literal, which needs no lookup.
        /// </param>
        Task<PingResult> SendPingAsync(
            string address,
            int timeoutMilliseconds,
            int resolveTimeoutMilliseconds,
            CancellationToken cancellationToken = default);
    }
}
