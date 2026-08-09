using System.Net;

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
        /// log, which is worse than a log that names the hop it can actually see. The same address
        /// is what <see cref="PasswordAttempts"/> counts guesses against, and for the same reason —
        /// a tally kept against a header anyone can set is no tally at all.
        /// </remarks>
        public static string Describe(HttpContext? context) =>
            Describe(context?.Connection.RemoteIpAddress);

        /// <inheritdoc cref="Describe(HttpContext?)"/>
        public static string Describe(IPAddress? address) =>
            address?.ToString() ?? "an unknown address";
    }
}
