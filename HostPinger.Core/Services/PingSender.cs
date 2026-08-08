using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Resolves an address and sends one ICMP echo to it. Nothing is thrown to the caller: a host
    /// that cannot be reached, or a name that cannot be resolved, is reported as an outcome so a
    /// single bad host cannot fail the round for every other host.
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

        public async Task<PingResult> SendPingAsync(
            string address,
            int timeoutMilliseconds,
            int resolveTimeoutMilliseconds,
            CancellationToken cancellationToken = default)
        {
            var destination = await ResolveAsync(address, resolveTimeoutMilliseconds, cancellationToken);
            if (destination is null)
            {
                return PingResult.Unresolved;
            }

            try
            {
                using var ping = new Ping();

                // Pinged by address rather than by name: the name has already been resolved, and
                // handing Ping the string would make it resolve a second time, on a wait this
                // timeout does not bound.
                var reply = await ping.SendPingAsync(
                    destination,
                    TimeSpan.FromMilliseconds(timeoutMilliseconds),
                    cancellationToken: cancellationToken);
                return reply.Status == IPStatus.Success
                    ? PingResult.Answered((int)reply.RoundtripTime)
                    : PingResult.Unanswered;
            }
            catch (Exception ex) when (ex is PingException or SocketException or ArgumentException)
            {
                // The address resolved, so there was a host to ask and the answer never came —
                // whether it stayed silent or the OS refused the socket. That is a missed ping.
                if (MayLog(address))
                {
                    _logger.LogWarning(ex, "Could not ping {Address}; recording it as unanswered.", address);
                }

                return PingResult.Unanswered;
            }
        }

        /// <summary>
        /// Turns the configured address into an IP, waiting no longer than its own timeout. Returns
        /// null when the name does not resolve, or does not resolve in time.
        /// </summary>
        /// <remarks>
        /// Cancelling the lookup stops this method waiting for it; the resolver call itself carries
        /// on in the background, because the platform resolvers are not interruptible. Bounding the
        /// wait is the point regardless — it is what keeps one host with a dead name from setting
        /// the pace of the whole round.
        /// </remarks>
        private async Task<IPAddress?> ResolveAsync(
            string address,
            int resolveTimeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            // An IP literal is already an address. Sending it through the resolver would only
            // invent a way for it to fail.
            if (IPAddress.TryParse(address, out var literal))
            {
                return literal;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(resolveTimeoutMilliseconds);

            try
            {
                // One address is all a ping needs, and the resolver returns its preferred family
                // first.
                var resolved = (await Dns.GetHostAddressesAsync(address, timeout.Token)).FirstOrDefault();
                if (resolved is null && MayLog(address))
                {
                    _logger.LogWarning("{Address} resolved to no addresses; skipping it this round.", address);
                }

                return resolved;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The round's own token was not the one that fired, so this is the resolve timeout.
                if (MayLog(address))
                {
                    _logger.LogWarning(
                        "Resolving {Address} took longer than {TimeoutMs}ms; skipping it this round.",
                        address,
                        resolveTimeoutMilliseconds);
                }

                return null;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                if (MayLog(address))
                {
                    _logger.LogWarning(ex, "Could not resolve {Address}; skipping it this round.", address);
                }

                return null;
            }
        }

        /// <summary>
        /// Whether this address may log a failure now, opening a quiet window if so. One window
        /// covers both failing to resolve and failing to ping: they are the same host being
        /// unreachable as far as a reader of the log is concerned.
        /// </summary>
        private bool MayLog(string address)
        {
            var now = _timeProvider.GetUtcNow();
            var quietUntil = _quietUntil.GetOrAdd(address, DateTimeOffset.MinValue);

            // TryUpdate is what makes this safe for the concurrent pings of a single round: hosts
            // are pinged in parallel, and only the caller that wins the exchange logs.
            return now >= quietUntil && _quietUntil.TryUpdate(address, now + FailureLogInterval, quietUntil);
        }
    }
}
