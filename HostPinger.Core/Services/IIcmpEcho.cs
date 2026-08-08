using System.Net;
using System.Net.NetworkInformation;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// What came back from an echo, reduced to the two things the caller reads. Exists because
    /// <see cref="PingReply"/> has no accessible constructor, so a test cannot produce one.
    /// </summary>
    /// <param name="Status">Whether the echo succeeded, and how it failed if not.</param>
    /// <param name="RoundtripMs">Round trip in milliseconds; meaningless unless the status is success.</param>
    public readonly record struct IcmpReply(IPStatus Status, long RoundtripMs);

    /// <summary>Sends one ICMP echo to an address.</summary>
    public interface IIcmpEcho
    {
        /// <summary>
        /// Echoes the destination once. The timeout covers the wait for a reply only — there is no
        /// name to look up by this point.
        /// </summary>
        Task<IcmpReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>The real echo. Deliberately holds no logic of its own.</summary>
    public sealed class SystemIcmpEcho : IIcmpEcho
    {
        public async Task<IcmpReply> SendAsync(
            IPAddress destination,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(destination, timeout, cancellationToken: cancellationToken);
            return new IcmpReply(reply.Status, reply.RoundtripTime);
        }
    }
}
