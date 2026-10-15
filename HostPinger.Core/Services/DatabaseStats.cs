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

        /// <summary>
        /// How many attempts there are, read off the span of their ids: the last id less the first,
        /// plus one, which is two seeks on the table's own key, where COUNT(*) is a scan of a whole
        /// index — most of a minute on a large database the operating system is not holding in
        /// memory, and a read that long keeps a ping round from committing until it gives up.
        /// </summary>
        /// <remarks>
        /// Each id is taken by a subquery of its own, because SQLite only seeks for a MIN or a MAX
        /// that stands alone: asked for both side by side, it scans the table for them. The span is
        /// the count exactly while the ids run unbroken, which they do: they are only ever handed
        /// out upwards, and the pruner removes the oldest. What it does count that is not there is
        /// the history of a deleted host, until pruning has passed it: too many by that host's
        /// share, which makes <see cref="BytesPerAttempt"/> too small by the same.
        /// </remarks>
        internal const string AttemptIdSpanSql =
            "SELECT COALESCE((SELECT MAX(\"Id\") FROM \"PingAttempts\") - (SELECT MIN(\"Id\") FROM \"PingAttempts\") + 1, 0);";

        /// <param name="db">The context to read through.</param>
        /// <param name="options">The settings the projection is made against.</param>
        /// <param name="cancellationToken">Cancels the queries.</param>
        public static async Task<DatabaseStats> CollectAsync(
            HostPingerDbContext db,
            PingerOptions options,
            CancellationToken cancellationToken = default)
        {
            return new DatabaseStats
            {
                SizeBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db, cancellationToken),
                MaxSizeBytes = options.MaxDatabaseSizeBytes,
                AttemptCount = await CountAttemptsAsync(db, cancellationToken),
                EnabledHostCount = await db.Hosts.CountAsync(h => h.IsEnabled, cancellationToken),
                IntervalSeconds = options.IntervalSeconds,
            };
        }

        /// <summary>How many attempts are stored, as <see cref="AttemptIdSpanSql"/> reads it.</summary>
        public static async Task<long> CountAttemptsAsync(
            HostPingerDbContext db,
            CancellationToken cancellationToken = default)
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = AttemptIdSpanSql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
    }
}
