using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Sends one ICMP echo per call. A failure is reported as "no reply" rather than thrown, so a
    /// single unreachable or misspelled host cannot fail the round for every other host.
    /// </summary>
    public sealed class PingSender : IPingSender
    {
        /// <summary>
        /// How long an address stays quiet after a failure is logged. Every enabled host is pinged
        /// on every round, so a host that is simply switched off would otherwise repeat the same
        /// warning at the configured interval indefinitely.
        /// </summary>
        private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(15);

        /// <summary>Address to the time its next failure may be logged.</summary>
        private readonly ConcurrentDictionary<string, DateTimeOffset> _quietUntil = new();

        private readonly ILogger<PingSender> _logger;
        private readonly TimeProvider _timeProvider;

        public PingSender(ILogger<PingSender> logger, TimeProvider? timeProvider = null)
        {
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<int?> SendPingAsync(string address, int timeoutMilliseconds, CancellationToken cancellationToken = default)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(
                    address,
                    TimeSpan.FromMilliseconds(timeoutMilliseconds),
                    cancellationToken: cancellationToken);
                return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
            }
            catch (Exception ex) when (ex is PingException or SocketException or ArgumentException)
            {
                // A throw means the ping never left the machine — an unresolvable name, or a socket
                // the OS refused — which is a different fault from a host that stayed silent, and
                // one the recorded attempt has no way to express.
                LogFailure(address, ex);
                return null;
            }
        }

        private void LogFailure(string address, Exception exception)
        {
            var now = _timeProvider.GetUtcNow();
            var quietUntil = _quietUntil.GetOrAdd(address, DateTimeOffset.MinValue);

            // TryUpdate is what makes this safe for the concurrent pings of a single round: hosts
            // are pinged in parallel, and only the caller that wins the exchange logs.
            if (now < quietUntil || !_quietUntil.TryUpdate(address, now + FailureLogInterval, quietUntil))
            {
                return;
            }

            _logger.LogWarning(exception, "Could not ping {Address}; recording it as unanswered.", address);
        }
    }
}
