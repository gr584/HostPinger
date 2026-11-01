using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        /// <summary>Where a round holding the database at its size can take back what it stored.</summary>
        private const string BeforeStoring = "BeforeStoring";

        private const long BytesPerMb = 1024 * 1024;

        private readonly IDbContextFactory<HostPingerDbContext> _dbFactory;
        private readonly IPingSender _pingSender;
        private readonly UserSettingsStore _settings;
        private readonly DatabasePruner _pruner;
        private readonly DatabaseSizeLimit _sizeLimit;
        private readonly MaintenanceGate _maintenanceGate;
        private readonly ILogger<PingMonitorService> _logger;
        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// Whether the last round held the database at its size, so that the log says when that
        /// starts and when it ends rather than on every round in between.
        /// </summary>
        private bool _holdingSize;

        /// <summary>Whether the free space has failed to measure, which the log says the first time only.</summary>
        private bool _roomUnmeasurable;

        /// <summary>
        /// How long a round may spend pruning, from the settings it runs with. Settable for the
        /// tests, which take it to nothing to see a round leave what is over the limit to the next.
        /// </summary>
        internal Func<PingerOptions, TimeSpan> PruneBudgetFor { get; init; } = PruneBudget;

        public PingMonitorService(
            IDbContextFactory<HostPingerDbContext> dbFactory,
            IPingSender pingSender,
            UserSettingsStore settings,
            DatabasePruner pruner,
            DatabaseSizeLimit sizeLimit,
            MaintenanceGate maintenanceGate,
            ILogger<PingMonitorService> logger,
            TimeProvider? timeProvider = null)
        {
            _dbFactory = dbFactory;
            _pingSender = pingSender;
            _settings = settings;
            _pruner = pruner;
            _sizeLimit = sizeLimit;
            _maintenanceGate = maintenanceGate;
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

        private TimeSpan GetInterval() => IntervalOf(_settings.CurrentPinger);

        private static TimeSpan IntervalOf(PingerOptions options) =>
            TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 1, MaxIntervalSeconds));

        /// <summary>
        /// How long a round may spend pruning: a quarter of the interval. Enough to keep up with
        /// many times what a round adds, and short enough that the rounds stay on time while a
        /// lowered limit is worked down to, a slice a round, rather than none running until it is
        /// reached; see <see cref="DatabasePruner.EnforceSizeLimitAsync"/>. A quarter rather than a
        /// fixed time, so a cut takes about as long to work through at any interval.
        /// </summary>
        internal static TimeSpan PruneBudget(PingerOptions options) => IntervalOf(options) / 4;

        /// <summary>
        /// Pings all enabled hosts once, stores the attempts, and prunes the database. Hosts whose
        /// address did not resolve are left out of the ping history rather than stored as
        /// unanswered — they are recorded as resolver errors instead — and so are hosts deleted
        /// while the round was pinging them, so the number returned can be smaller than the number
        /// of enabled hosts.
        /// </summary>
        public async Task<int> RunRoundAsync(CancellationToken cancellationToken = default)
        {
            // Taken per round rather than for the loop's lifetime, so a backup or restore holding
            // the gate waits out at most one round and then has the file to itself.
            using var maintenanceHold = await _maintenanceGate.AcquireAsync(cancellationToken);

            var options = _settings.CurrentPinger;
            var holdSize = HoldsSize();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var timestampUtc = _timeProvider.GetUtcNow().UtcDateTime;

            var recorded = await PingAndStoreAsync(db, options, timestampUtc, holdSize, cancellationToken);

            // Pruning runs whether or not there was anything to ping. A service whose hosts have
            // all been deleted or paused still holds a database, and what has aged out of the
            // resolver errors has aged out regardless — tying that to a round having recorded
            // something would leave the last failures of a host that is gone sitting there for
            // good. The size limit still gives what it prunes back to the disk while the database
            // is held: the file does not grow then, so it is only over the limit once someone
            // lowers it, and lowering it is how they free the disk.
            await _pruner.EnforceResolverErrorRetentionAsync(db, timestampUtc, reclaimSpace: !holdSize, cancellationToken);
            await _pruner.EnforceSizeLimitAsync(db, options.MaxDatabaseSizeBytes, PruneBudgetFor(options), cancellationToken);
            return recorded;
        }

        /// <summary>
        /// Whether this round holds the database at its size, which it does while the disk is nearly
        /// full (see <see cref="DatabaseRoom.IsDiskNearlyFull"/>). The log has a warning when that
        /// starts and a line when it ends.
        /// </summary>
        /// <remarks>
        /// A free space that will not measure leaves the round as it was before there was a hold,
        /// with a warning the first time: a check that only decides how the round makes room is no
        /// reason to stop the monitoring.
        /// </remarks>
        private bool HoldsSize()
        {
            DatabaseRoom room;
            try
            {
                room = _sizeLimit.MeasureRoom();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (!_roomUnmeasurable)
                {
                    _roomUnmeasurable = true;
                    _logger.LogWarning(
                        ex,
                        "The free space beside the database will not measure, so the rounds cannot tell whether the "
                        + "disk is nearly full.");
                }

                return false;
            }

            var hold = room.IsDiskNearlyFull;
            if (hold != _holdingSize)
            {
                _holdingSize = hold;
                if (hold)
                {
                    _logger.LogWarning(
                        "The disk beside the database has {FreeMb} MB free, less than the {MarginMb} MB kept in hand, so the "
                        + "database is held at its size: each round deletes the oldest history it needs to make room for what "
                        + "it records. Free space on the disk to let it grow again.",
                        room.FreeBytes / BytesPerMb,
                        DatabaseBackup.FreeSpaceMarginBytes / BytesPerMb);
                }
                else
                {
                    _logger.LogInformation(
                        "The disk beside the database has {FreeMb} MB free again, so the database may grow to its size limit.",
                        room.FreeBytes / BytesPerMb);
                }
            }

            return hold;
        }

        /// <summary>
        /// Pings every enabled host once and stores what came of it, returning the number of ping
        /// attempts recorded. Nothing to ping is not a failure: it stores nothing and returns zero.
        /// </summary>
        private async Task<int> PingAndStoreAsync(
            HostPingerDbContext db,
            PingerOptions options,
            DateTime timestampUtc,
            bool holdSize,
            CancellationToken cancellationToken)
        {
            var hosts = await db.Hosts.AsNoTracking().Where(h => h.IsEnabled).ToListAsync(cancellationToken);
            if (hosts.Count == 0)
            {
                return 0;
            }

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
            // nothing to say about its reachability. Storing it as unanswered would show up as
            // downtime later — an outage invented out of a name that does not resolve — so it stays
            // out of the ping history.
            var attempts = results
                .Where(outcome => outcome.result.IsRecordablePing)
                .Select(outcome => new PingAttempt
                {
                    HostId = outcome.host.Id,
                    TimestampUtc = timestampUtc,
                    RoundtripMs = outcome.result.RoundtripMs,
                })
                .ToList();

            // The failed lookup is still worth having: it is the reason the host is missing from
            // the history for this round, and reading it anywhere else means going through the log.
            // Recorded against the address rather than the host for the reasons on ResolverError.
            var resolverErrors = results
                .Where(outcome => outcome.result.Failure is not null)
                .Select(outcome => new ResolverError
                {
                    Address = outcome.host.Address,
                    TimestampUtc = timestampUtc,
                    Reason = outcome.result.Failure!.Value,
                })
                .ToList();

            // The hosts were read before the pings, and those take seconds, long enough for one of
            // them to be deleted: the Hosts page does not wait for a round, since a delete that did
            // would sit behind the slowest ping in it. An attempt for a host that has gone has no
            // row to point at, and the foreign key would refuse the whole save with it, losing every
            // other host's ping too. So the round asks which hosts are still there and saves the
            // attempts of those. It asks inside the transaction that saves them, which
            // Microsoft.Data.Sqlite begins IMMEDIATE, taking the write lock before the question, so
            // that no delete can land between the answer and the save; a deferred one would take it
            // only at the first write, too late. The failed lookups are recorded against the address
            // rather than the host, and stay.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var remaining = (await db.Hosts.Select(h => h.Id).ToListAsync(cancellationToken)).ToHashSet();
            attempts.RemoveAll(attempt => !remaining.Contains(attempt.HostId));

            db.PingAttempts.AddRange(attempts);
            db.ResolverErrors.AddRange(resolverErrors);
            if (holdSize)
            {
                await SaveHoldingSizeAsync(db, transaction, attempts, resolverErrors, cancellationToken);
            }
            else
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return attempts.Count;
        }

        /// <summary>
        /// Saves what <see cref="PingAndStoreAsync"/> added without growing the file, for a round
        /// holding the database at its size: into pages the file already has free, and where those
        /// are too few, into the pages a batch of the oldest history leaves once it is deleted. The
        /// rows go in, and if they took the file past its size they come out again, the batch is
        /// deleted, and they go in again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deleting only once the rows do not fit, rather than as many as each round adds, has the
        /// rounds use up whatever room the file has before any history goes. The history held
        /// settles a little shorter at first, as the indexes fill to how they stay under turnover,
        /// and from then on goes no faster than it arrives. Nothing here vacuums, so the space
        /// deleted stays inside the file, where nothing but the database can take it.
        /// </para>
        /// <para>
        /// The size is taken again before each save, after any delete, because a delete can take a
        /// page of its own: replacing a key in an index's interior page with a longer one splits the
        /// page, now and then, before the delete has freed any. Measured against the file as it was
        /// before that, the rows would never fit again however much was deleted, and one round went
        /// on until the whole history was gone. So the file can grow by such a page now and then, and
        /// the rows still only go where there is room.
        /// </para>
        /// <para>
        /// Inside the round's transaction, so a round that fails deletes nothing. The size is read
        /// before the round commits, so it is the file's own growth that makes room, and never
        /// SQLite's -wal failing to grow on a disk with nothing free at all: deleting would not
        /// help that, since the delete needs the -wal too. A file with no history left to delete
        /// grows as it would have before.
        /// </para>
        /// </remarks>
        private async Task SaveHoldingSizeAsync(
            HostPingerDbContext db,
            IDbContextTransaction transaction,
            List<PingAttempt> attempts,
            List<ResolverError> resolverErrors,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var sizeBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db, cancellationToken);
                await transaction.CreateSavepointAsync(BeforeStoring, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                if (await DatabasePruner.GetDatabaseSizeBytesAsync(db, cancellationToken) <= sizeBytes)
                {
                    return;
                }

                // Back out, and added again as rows not yet stored: the save gave each the id it
                // went in under, and the ids are handed out again once there is room.
                await transaction.RollbackToSavepointAsync(BeforeStoring, cancellationToken);
                db.ChangeTracker.Clear();
                attempts.ForEach(attempt => attempt.Id = 0);
                resolverErrors.ForEach(error => error.Id = 0);
                db.PingAttempts.AddRange(attempts);
                db.ResolverErrors.AddRange(resolverErrors);

                if (await _pruner.DeleteOldestAsync(db, cancellationToken) == 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
            }
        }
    }
}
