namespace HostPinger.Security
{
    /// <summary>Says where a request came from, for the lines the password pages write to the log.</summary>
    internal static class RequestOrigin
    {
        /// <summary>
        /// The address the connection was opened from.
        /// </summary>
        /// <remarks>
        /// Behind a reverse proxy that is the proxy rather than the browser, because nothing here
        /// reads forwarded headers: taking those on trust without a list of proxies to trust them
        /// from would let anyone writing to this service put whatever address they liked into the
        /// log, which is worse than a log that names the hop it can actually see.
        /// </remarks>
        public static string Describe(HttpContext? context) =>
            context?.Connection.RemoteIpAddress?.ToString() ?? "an unknown address";
    }
}
