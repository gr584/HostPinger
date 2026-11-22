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

        /// <summary>The file's size, which is what the size limit is held to.</summary>
        public required long SizeBytes { get; init; }

        /// <summary>
        /// What the file's pages in use come to: <see cref="SizeBytes"/> less the free pages
        /// inside it, which hold no data. An upgrade that rebuilds a table can leave many of them,
        /// until later writes reuse them or pruning gives them back to the disk.
        /// </summary>
        public required long UsedBytes { get; init; }

        /// <summary>
        /// The limit in force. Never zero or less once the service has started (see
        /// <see cref="DatabaseSizeLimit.SettleAsync"/>); where it is, nothing is projected.
        /// </summary>
        public required long MaxSizeBytes { get; init; }

        public required long AttemptCount { get; init; }

        public required int EnabledHostCount { get; init; }

        public required int IntervalSeconds { get; init; }

        /// <summary>Attempts recorded per day at the current host count and ping interval.</summary>
        public double AttemptsPerDay => EnabledHostCount <= 0 || IntervalSeconds <= 0
            ? 0
            : EnabledHostCount * TimeSpan.FromDays(1).TotalSeconds / IntervalSeconds;

        /// <summary>
        /// Average bytes each stored attempt accounts for, or null while unknown. Measured on the
        /// pages in use rather than on the whole file, whose free pages hold no attempts: counted,
        /// they would make each attempt look larger than it is, and the history the limit holds
        /// shorter, by as much as the file has free.
        /// </summary>
        public double? BytesPerAttempt => AttemptCount < MinimumAttemptsForEstimate || UsedBytes <= BaselineBytes
            ? null
            : (double)(UsedBytes - BaselineBytes) / AttemptCount;

        /// <summary>Projected growth of the file per day, or null while it cannot be estimated.</summary>
        public double? GrowthBytesPerDay => AttemptsPerDay <= 0 ? null : BytesPerAttempt * AttemptsPerDay;

        /// <summary>
        /// How many days of history fit inside <see cref="MaxSizeBytes"/> at the projected growth.
        /// Null when growth is unknown, or when there is no limit to project against.
        /// </summary>
        public double? CapacityDays => CapacityDaysIn(MaxSizeBytes);

        /// <summary>
        /// How many days of history fit inside <paramref name="sizeBytes"/> at the projected growth:
        /// the limit's, or while the disk is nearly full and the database is held at its size, the
        /// file's own (see <see cref="DatabaseRoom.IsDiskNearlyFull"/>). Null when growth is
        /// unknown, or when there is no size to project against.
        /// </summary>
        public double? CapacityDaysIn(long sizeBytes) => sizeBytes <= 0 || GrowthBytesPerDay is not > 0
            ? null
            : sizeBytes / GrowthBytesPerDay;

        /// <summary>What <see cref="UsedBytes"/> is read with: the pages in use, the free ones left out.</summary>
        internal const string UsedBytesSql =
            "SELECT (page_count - freelist_count) * page_size FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size();";

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
        internal static async Task<DatabaseStats> CollectAsync(
            HostPingerDbContext db,
            PingerOptions options,
            CancellationToken cancellationToken = default)
        {
            return new DatabaseStats
            {
                SizeBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db, cancellationToken),
                UsedBytes = await ScalarAsync(db, UsedBytesSql, cancellationToken),
                MaxSizeBytes = options.MaxDatabaseSizeBytes,
                AttemptCount = await CountAttemptsAsync(db, cancellationToken),
                EnabledHostCount = await db.Hosts.CountAsync(h => h.IsEnabled, cancellationToken),
                IntervalSeconds = options.IntervalSeconds,
            };
        }

        /// <summary>How many attempts are stored, as <see cref="AttemptIdSpanSql"/> reads it.</summary>
        internal static Task<long> CountAttemptsAsync(
            HostPingerDbContext db,
            CancellationToken cancellationToken = default) =>
            ScalarAsync(db, AttemptIdSpanSql, cancellationToken);

        private static async Task<long> ScalarAsync(HostPingerDbContext db, string sql, CancellationToken cancellationToken)
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
    }
}
