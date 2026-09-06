using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// A snapshot of how much room the database is using and how fast it is filling up. Growth is
    /// projected from the average size of the attempts already stored and the rate at which new
    /// ones are recorded — one per enabled host per ping interval.
    /// </summary>
    public sealed record DatabaseStats
    {
        /// <summary>
        /// Size of a freshly migrated, empty database: schema, indexes, migration history and the
        /// auto-vacuum pointer map. Subtracted before averaging so a nearly-empty file does not
        /// make each attempt look enormous.
        /// </summary>
        public const long BaselineBytes = 44 * 1024;

        /// <summary>
        /// Below this many attempts the baseline dominates the file and the average is too noisy
        /// to project from, so growth is reported as unknown instead.
        /// </summary>
        public const long MinimumAttemptsForEstimate = 200;

        public required long SizeBytes { get; init; }

        /// <summary>The configured limit; zero or less means pruning is disabled.</summary>
        public required long MaxSizeBytes { get; init; }

        public required long AttemptCount { get; init; }

        public required int EnabledHostCount { get; init; }

        public required int IntervalSeconds { get; init; }

        /// <summary>Attempts recorded per day at the current host count and ping interval.</summary>
        public double AttemptsPerDay => EnabledHostCount <= 0 || IntervalSeconds <= 0
            ? 0
            : EnabledHostCount * TimeSpan.FromDays(1).TotalSeconds / IntervalSeconds;

        /// <summary>Average bytes each stored attempt accounts for, or null while unknown.</summary>
        public double? BytesPerAttempt => AttemptCount < MinimumAttemptsForEstimate || SizeBytes <= BaselineBytes
            ? null
            : (double)(SizeBytes - BaselineBytes) / AttemptCount;

        /// <summary>Projected growth of the file per day, or null while it cannot be estimated.</summary>
        public double? GrowthBytesPerDay => AttemptsPerDay <= 0 ? null : BytesPerAttempt * AttemptsPerDay;

        /// <summary>
        /// How many days of history fit inside <see cref="MaxSizeBytes"/> at the projected growth.
        /// Null when growth is unknown, or when pruning is disabled and capacity is unbounded.
        /// </summary>
        public double? CapacityDays => MaxSizeBytes <= 0 || GrowthBytesPerDay is not > 0
            ? null
            : MaxSizeBytes / GrowthBytesPerDay;

        /// <param name="db">The context to read through.</param>
        /// <param name="options">The settings the projection is made against.</param>
        /// <param name="knownAttemptCount">
        /// An attempt count already taken, or null to take one now. Everything else here is a
        /// pragma or an index seek; this is the one figure that costs a scan of the whole table,
        /// because SQLite has no cheap COUNT(*) — seconds, on a database near its size limit. It
        /// only feeds <see cref="BytesPerAttempt"/>, an average over the whole history that no
        /// single round moves, so a caller refreshing these figures on a timer should re-take it on
        /// a far slower one than the rest and pass it in between.
        /// </param>
        /// <param name="cancellationToken">Cancels the queries.</param>
        public static async Task<DatabaseStats> CollectAsync(
            HostPingerDbContext db,
            PingerOptions options,
            long? knownAttemptCount = null,
            CancellationToken cancellationToken = default)
        {
            return new DatabaseStats
            {
                SizeBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db, cancellationToken),
                MaxSizeBytes = options.MaxDatabaseSizeBytes,
                AttemptCount = knownAttemptCount ?? await CountAttemptsAsync(db, cancellationToken),
                EnabledHostCount = await db.Hosts.CountAsync(h => h.IsEnabled, cancellationToken),
                IntervalSeconds = options.IntervalSeconds,
            };
        }

        /// <summary>
        /// The expensive half of <see cref="CollectAsync"/> on its own, so a caller that refreshes
        /// it on its own cadence has somewhere to ask for it.
        /// </summary>
        public static Task<long> CountAttemptsAsync(
            HostPingerDbContext db,
            CancellationToken cancellationToken = default) =>
            db.PingAttempts.LongCountAsync(cancellationToken);
    }
}
