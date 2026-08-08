using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HostPinger.Core.Services
{
    /// <summary>Periodically pings every enabled host and records the results.</summary>
    public class PingMonitorService : BackgroundService
    {
        /// <summary>Guards against a nonsensical configured interval; one day is well past useful.</summary>
        private const int MaxIntervalSeconds = 86_400;

        /// <summary>
        /// Guards against a nonsensical configured timeout; a minute is well past useful, and Ping
        /// rejects a negative one outright.
        /// </summary>
        private const int MaxTimeoutSeconds = 60;

        private readonly IDbContextFactory<HostPingerDbContext> _dbFactory;
        private readonly IPingSender _pingSender;
        private readonly IOptionsMonitor<PingerOptions> _options;
        private readonly DatabasePruner _pruner;
        private readonly ILogger<PingMonitorService> _logger;
        private readonly TimeProvider _timeProvider;

        public PingMonitorService(
            IDbContextFactory<HostPingerDbContext> dbFactory,
            IPingSender pingSender,
            IOptionsMonitor<PingerOptions> options,
            DatabasePruner pruner,
            ILogger<PingMonitorService> logger,
            TimeProvider? timeProvider = null)
        {
            _dbFactory = dbFactory;
            _pingSender = pingSender;
            _options = options;
            _pruner = pruner;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var period = GetInterval();
            using var timer = new PeriodicTimer(period, _timeProvider);
            try
            {
                do
                {
                    try
                    {
                        await RunRoundAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ping round failed.");
                    }

                    // Picks up an interval changed on the Configuration page; the new period
                    // applies from the next tick onwards. Assigned only when it actually changed,
                    // because assigning restarts the countdown from this moment: doing it after
                    // every round would push that round's duration into the gap, so a host slow
                    // enough to take three seconds would stretch a ten second interval to
                    // thirteen. Left alone, the period absorbs the round instead.
                    var configured = GetInterval();
                    if (configured != period)
                    {
                        period = configured;
                        timer.Period = period;
                    }
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private TimeSpan GetInterval() =>
            TimeSpan.FromSeconds(Math.Clamp(_options.CurrentValue.IntervalSeconds, 1, MaxIntervalSeconds));

        /// <summary>
        /// Pings all enabled hosts once, stores the attempts, and prunes the database. Hosts whose
        /// address did not resolve are left out rather than stored as unanswered, so the number
        /// returned can be smaller than the number of enabled hosts.
        /// </summary>
        public async Task<int> RunRoundAsync(CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var hosts = await db.Hosts.AsNoTracking().Where(h => h.IsEnabled).ToListAsync(cancellationToken);
            if (hosts.Count == 0)
            {
                return 0;
            }

            var timestampUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var timeoutMilliseconds = Math.Clamp(options.TimeoutSeconds, 1, MaxTimeoutSeconds) * 1000;
            var resolveTimeoutMilliseconds = Math.Clamp(options.ResolveTimeoutSeconds, 1, MaxTimeoutSeconds) * 1000;

            var results = await Task.WhenAll(hosts.Select(async host => (
                host,
                result: await _pingSender.SendPingAsync(
                    host.Address,
                    timeoutMilliseconds,
                    resolveTimeoutMilliseconds,
                    cancellationToken))));

            // A host whose address would not resolve was never asked anything, so the round has
            // nothing to say about it and records nothing. Storing it as unanswered would show up
            // as downtime later — an outage invented out of a name that does not resolve.
            var attempts = results
                .Where(outcome => outcome.result.IsRecordable)
                .Select(outcome => new PingAttempt
                {
                    HostId = outcome.host.Id,
                    TimestampUtc = timestampUtc,
                    RoundtripMs = outcome.result.RoundtripMs,
                })
                .ToList();

            db.PingAttempts.AddRange(attempts);
            await db.SaveChangesAsync(cancellationToken);

            await _pruner.EnforceSizeLimitAsync(db, options.MaxDatabaseSizeBytes, cancellationToken);
            return attempts.Count;
        }
    }
}
