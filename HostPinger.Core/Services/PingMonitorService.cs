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
        private readonly IDbContextFactory<HostPingerDbContext> _dbFactory;
        private readonly IPingSender _pingSender;
        private readonly IOptions<PingerOptions> _options;
        private readonly DatabasePruner _pruner;
        private readonly ILogger<PingMonitorService> _logger;
        private readonly TimeProvider _timeProvider;

        public PingMonitorService(
            IDbContextFactory<HostPingerDbContext> dbFactory,
            IPingSender pingSender,
            IOptions<PingerOptions> options,
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
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Value.IntervalSeconds));
            using var timer = new PeriodicTimer(interval, _timeProvider);
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
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Pings all enabled hosts once, stores the attempts, and prunes the database.
        /// Returns the number of attempts recorded.
        /// </summary>
        public async Task<int> RunRoundAsync(CancellationToken cancellationToken = default)
        {
            var options = _options.Value;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var hosts = await db.Hosts.AsNoTracking().Where(h => h.IsEnabled).ToListAsync(cancellationToken);
            if (hosts.Count == 0)
            {
                return 0;
            }

            var timestampUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var attempts = await Task.WhenAll(hosts.Select(async host => new PingAttempt
            {
                HostId = host.Id,
                TimestampUtc = timestampUtc,
                RoundtripMs = await _pingSender.SendPingAsync(host.Address, options.TimeoutMilliseconds, cancellationToken),
            }));

            db.PingAttempts.AddRange(attempts);
            await db.SaveChangesAsync(cancellationToken);

            await _pruner.EnforceSizeLimitAsync(db, options.MaxDatabaseSizeBytes, cancellationToken);
            return attempts.Length;
        }
    }
}
