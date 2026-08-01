using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Pings loopback once at startup to establish that this process is allowed to send ICMP at
    /// all, and says so in the log.
    /// </summary>
    /// <remarks>
    /// A denied ICMP socket and a genuinely unreachable host are indistinguishable downstream:
    /// <see cref="PingSender"/> reports both as an unanswered attempt, so the UI shows every host as
    /// down. That is the state of any Linux host granting neither <c>CAP_NET_RAW</c> nor a
    /// <c>net.ipv4.ping_group_range</c> that covers the service account, and without this check it
    /// reads as a total outage rather than as a deployment mistake.
    ///
    /// The denial is not reliably an exception. Where the platform can neither open a raw socket nor
    /// fall back to anything usable, the send is accepted and simply never answered, so a
    /// non-<see cref="IPStatus.Success"/> reply from loopback is treated as conclusive rather than as
    /// a lesser warning.
    /// </remarks>
    public sealed class IcmpAvailabilityCheck(ILogger<IcmpAvailabilityCheck> logger) : IHostedService
    {
        /// <summary>Loopback always answers when the socket itself is permitted.</summary>
        private const string ProbeAddress = "127.0.0.1";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Appended to both failure reports, which differ only in how the probe failed.
        /// </summary>
        private const string Remedy =
            " Every host will be recorded as down regardless of whether it is reachable. On Linux the service "
            + "needs the CAP_NET_RAW capability, which the packaged systemd unit grants with "
            + "AmbientCapabilities=CAP_NET_RAW; failing that, net.ipv4.ping_group_range has to include the "
            + "group the service runs as.";

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            IPStatus? status = null;
            Exception? failure = null;

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ProbeAddress, ProbeTimeout, cancellationToken: cancellationToken);
                status = reply.Status;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PingException or SocketException or PlatformNotSupportedException)
            {
                failure = ex;
            }

            if (status == IPStatus.Success)
            {
                logger.LogInformation("ICMP is available; ping results reflect host reachability.");
            }
            else if (failure is not null)
            {
                logger.LogError(failure, "ICMP is not usable by this process." + Remedy);
            }
            else
            {
                // Not a lesser problem than the exception above: a refused ICMP socket surfaces on
                // Linux as a reply that never arrives rather than as a throw, so this is the branch
                // the common misconfiguration actually takes. Loopback answers whenever ICMP works
                // at all, which is what makes a non-Success status here conclusive.
                logger.LogError(
                    "ICMP is not usable by this process: the loopback probe returned {Status}." + Remedy,
                    status);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
