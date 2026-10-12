using System.Data.Common;
using HostPinger.Core.Charting;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    /// <summary>
    /// One host's recorded attempts, reduced to the points a chart draws over a range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reduction happens in the database rather than in memory, which is the whole point of
    /// this type. A chart is a few hundred points wide however much time it covers, so the range
    /// behind those points is bounded by nothing at all: opening the graph on a long outage asked
    /// for every attempt in it — five and a half million rows on a development database — and then
    /// averaged them down to six hundred. Reading them cost thirteen seconds, almost none of it
    /// spent by SQLite, which walks that range in well under one; the cost was materialising a row
    /// object per attempt and then a sample per row on top of it.
    /// </para>
    /// <para>
    /// So each bucket is asked for as a count and a sum over an indexed range, and what comes back
    /// is one row per bucket. That is one statement per bucket, which sounds worse than it
    /// measures: the same index range is walked either way, and the per-statement cost against it
    /// is small enough that the whole read stays near a second from fifty buckets to eight
    /// thousand — the count now barely moves the total, where before it was the row count that
    /// decided it.
    /// </para>
    /// <para>
    /// What a reduced series is made of lives in <see cref="ChartMath"/> and is shared with
    /// <see cref="ChartMath.Downsample"/>, which reduces the same way over samples it already
    /// holds: <see cref="ChartMath.BucketDuration"/> and <see cref="ChartMath.BucketIndex"/> lay
    /// the grid down, and <see cref="ChartMath.BucketSample"/> says where each bucket reports and
    /// what it reports. What is left here is the half that cannot be shared — asking the database
    /// for the totals rather than counting them in memory, and skipping a bucket nothing was
    /// recorded in, which this has to test for and a fold over samples gets for free.
    /// <c>ChartSeriesTests</c> reduces the same attempts both ways and compares, so the two halves
    /// cannot drift apart.
    /// </para>
    /// <para>
    /// A live chart reads its range again every five seconds, and over a long range that is
    /// hundreds of statements to learn what changed in one bucket: the graph opened on a two-month
    /// outage spent a third of a second of every refresh on it, seven percent of a core for as
    /// long as it stayed open. <see cref="RefreshAsync"/> reads from the newest bucket a read
    /// already holds and carries the rest over, so a refresh costs the bucket or two still filling.
    /// </para>
    /// </remarks>
    public static class ChartSeries
    {
        /// <summary>
        /// One aggregate over one bucket. Every column is answered off
        /// IX_PingAttempts_HostId_TimestampUtc as a range seek, and the sum and the count of
        /// answered pings are what the bucket's average is made of.
        /// </summary>
        private const string BucketSql =
            """
            SELECT COUNT(*), COALESCE(SUM(RoundtripMs), 0), COUNT(RoundtripMs)
            FROM PingAttempts
            WHERE HostId = @hostId AND TimestampUtc >= @from AND TimestampUtc < @to;
            """;

        /// <summary>Reads one host's series over a range, from scratch.</summary>
        /// <param name="db">The context to read through.</param>
        /// <param name="hostId">The host to read.</param>
        /// <param name="rangeStartUtc">The oldest instant on the chart.</param>
        /// <param name="rangeEndUtc">The newest.</param>
        /// <param name="bucketCount">How many points the chart has room for.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public static async Task<ChartSeriesRead> LoadAsync(
            HostPingerDbContext db,
            int hostId,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int bucketCount,
            CancellationToken cancellationToken = default)
        {
            var (bucketDuration, queryStart) = Grid(rangeStartUtc, rangeEndUtc, bucketCount);
            if (await FitsAsRecordedAsync(db, hostId, queryStart, rangeEndUtc, bucketCount, cancellationToken))
            {
                return await ReadAsRecordedAsync(
                    db, hostId, queryStart, rangeStartUtc, rangeEndUtc, bucketCount, bucketDuration, cancellationToken);
            }

            return new ChartSeriesRead(
                await AggregateAsync(db, hostId, queryStart, rangeEndUtc, bucketDuration, bucketCount, cancellationToken),
                rangeStartUtc,
                rangeEndUtc,
                bucketDuration);
        }

        /// <summary>
        /// Reads one host's series over a range a live chart has slid along since
        /// <paramref name="earlier"/> was read, by carrying that read on rather than starting over:
        /// the buckets from the newest one it holds to the end of the range are read, the ones
        /// before that are taken from it as they were, and the ones the range has slid past are
        /// dropped. A read it cannot carry on from is read from scratch, as
        /// <see cref="LoadAsync"/> would.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What comes back is what <see cref="LoadAsync"/> would read over the same range, for as
        /// long as nothing at or before the newest point <paramref name="earlier"/> holds has
        /// changed since. The buckets sit on a grid fixed in absolute time, so a bucket the range
        /// still covers is read the same way whenever it is read, and every bucket but the one the
        /// present is in reports from the same place whatever the range ends at; <c>ChartMathTests</c>
        /// holds the in-memory reduction to both. Attempts recorded between two reads arrive in
        /// order, too: a round stamps one time on everything it records and saves it in one go, and
        /// rounds follow one another, so nothing that lands after a read can be older than the
        /// newest attempt that read saw — which is in the newest bucket it holds, where this starts.
        /// </para>
        /// <para>
        /// What breaks that is rows changing behind that point: the pruner trimming the oldest of
        /// the history, a restore replacing the database underneath, a clock stepping backwards. A
        /// carried-on series goes on showing what it held until its next read from scratch, so a
        /// caller refreshing on a timer should still read from scratch on a slower one.
        /// </para>
        /// </remarks>
        /// <param name="db">The context to read through.</param>
        /// <param name="hostId">The host to read, the one <paramref name="earlier"/> was read for.</param>
        /// <param name="earlier">The read to carry on from.</param>
        /// <param name="rangeStartUtc">The oldest instant on the chart.</param>
        /// <param name="rangeEndUtc">The newest.</param>
        /// <param name="bucketCount">How many points the chart has room for.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public static async Task<ChartSeriesRead> RefreshAsync(
            HostPingerDbContext db,
            int hostId,
            ChartSeriesRead earlier,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int bucketCount,
            CancellationToken cancellationToken = default)
        {
            var (bucketDuration, queryStart) = Grid(rangeStartUtc, rangeEndUtc, bucketCount);
            var firstBucket = ChartMath.BucketIndex(queryStart, bucketDuration);

            // Carried on only from buckets on this same grid, reaching back at least as far as this
            // range does and no further forward than it ends. A resize or a different window lays a
            // grid of its own, a range that has grown backwards needs buckets the earlier read never
            // had, and an end that has gone backwards is a clock that did.
            var canCarryOn = earlier.BucketDuration == bucketDuration
                && earlier.Samples.Count > 0
                && ChartMath.BucketIndex(earlier.RangeStartUtc, bucketDuration) <= firstBucket
                && earlier.RangeEndUtc <= rangeEndUtc;
            if (!canCarryOn)
            {
                return await LoadAsync(db, hostId, rangeStartUtc, rangeEndUtc, bucketCount, cancellationToken);
            }

            // The same question LoadAsync asks first, since a range that has slid on far enough can
            // come to hold few enough attempts to draw as they were recorded.
            if (await FitsAsRecordedAsync(db, hostId, queryStart, rangeEndUtc, bucketCount, cancellationToken))
            {
                return await ReadAsRecordedAsync(
                    db, hostId, queryStart, rangeStartUtc, rangeEndUtc, bucketCount, bucketDuration, cancellationToken);
            }

            // From the newest bucket the earlier read holds, unless the range has slid past even that.
            var readFrom = Math.Max(ChartMath.BucketIndex(earlier.Samples[^1].TimestampUtc, bucketDuration), firstBucket);
            var carried = earlier.Samples.Where(sample =>
                ChartMath.BucketIndex(sample.TimestampUtc, bucketDuration) is var bucket
                && bucket >= firstBucket
                && bucket < readFrom);
            var read = await AggregateAsync(
                db,
                hostId,
                new DateTime(readFrom * bucketDuration.Ticks, DateTimeKind.Utc),
                rangeEndUtc,
                bucketDuration,
                bucketCount,
                cancellationToken);

            return new ChartSeriesRead([.. carried, .. read], rangeStartUtc, rangeEndUtc, bucketDuration);
        }

        /// <summary>The grid a read over the range buckets onto, and where reading it starts.</summary>
        private static (TimeSpan BucketDuration, DateTime QueryStartUtc) Grid(
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int bucketCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
            if (rangeEndUtc <= rangeStartUtc)
            {
                throw new ArgumentException("rangeEndUtc must be after rangeStartUtc.", nameof(rangeEndUtc));
            }

            var bucketDuration = ChartMath.BucketDuration(rangeStartUtc, rangeEndUtc, bucketCount);

            // The oldest bucket on screen starts before the range does, and reading only from the
            // range would leave it averaging whatever part of itself has not been slid past yet;
            // see ChartMath.BucketStart, which is where that reasoning lives.
            return (bucketDuration, ChartMath.BucketStart(rangeStartUtc, bucketDuration));
        }

        /// <summary>
        /// Whether the range holds few enough attempts to draw as they were recorded. Counted no
        /// further than one past what would fit, which is all the question asks: a range holding a
        /// year of attempts must not be walked end to end just to be told it holds more than six
        /// hundred.
        /// </summary>
        private static async Task<bool> FitsAsRecordedAsync(
            HostPingerDbContext db,
            int hostId,
            DateTime queryStartUtc,
            DateTime rangeEndUtc,
            int bucketCount,
            CancellationToken cancellationToken) =>
            await db.PingAttempts.AsNoTracking()
                .Where(a => a.HostId == hostId && a.TimestampUtc >= queryStartUtc && a.TimestampUtc <= rangeEndUtc)
                .Take(bucketCount + 1)
                .CountAsync(cancellationToken) <= bucketCount;

        /// <summary>
        /// The attempts themselves, for a range with few enough of them to draw as they were
        /// recorded. Downsample hands these back untouched so the hover readout keeps the exact
        /// ping times rather than bucket midpoints, and it is still the one place that rule is
        /// written down.
        /// </summary>
        private static async Task<ChartSeriesRead> ReadAsRecordedAsync(
            HostPingerDbContext db,
            int hostId,
            DateTime queryStartUtc,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int bucketCount,
            TimeSpan bucketDuration,
            CancellationToken cancellationToken)
        {
            var samples = await db.PingAttempts.AsNoTracking()
                .Where(a => a.HostId == hostId && a.TimestampUtc >= queryStartUtc && a.TimestampUtc <= rangeEndUtc)
                .OrderBy(a => a.TimestampUtc)
                .Select(a => new ChartSample(a.TimestampUtc, a.RoundtripMs))
                .ToListAsync(cancellationToken);

            // A round can land between the count and this read and tip the range over what fits, in
            // which case Downsample buckets the attempts after all. It does so onto the grid the
            // database reduction uses, which is what lets a later read carry them on as buckets.
            return new ChartSeriesRead(
                ChartMath.Downsample(samples, rangeStartUtc, rangeEndUtc, bucketCount),
                rangeStartUtc,
                rangeEndUtc,
                samples.Count <= bucketCount ? null : bucketDuration);
        }

        private static async Task<IReadOnlyList<ChartSample>> AggregateAsync(
            HostPingerDbContext db,
            int hostId,
            DateTime queryStartUtc,
            DateTime rangeEndUtc,
            TimeSpan bucketDuration,
            int bucketCount,
            CancellationToken cancellationToken)
        {
            var bucketTicks = bucketDuration.Ticks;
            var firstBucket = ChartMath.BucketIndex(queryStartUtc, bucketDuration);
            var lastBucket = ChartMath.BucketIndex(rangeEndUtc, bucketDuration);

            // The range end is the last instant the chart draws rather than the first it does not,
            // so the newest bucket is closed just past it.
            var endExclusiveTicks = Math.Min(rangeEndUtc.Ticks + 1, DateTime.MaxValue.Ticks);

            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = BucketSql;
                var host = Parameter(command, "@hostId", hostId);
                var from = Parameter(command, "@from", queryStartUtc);
                var to = Parameter(command, "@to", queryStartUtc);

                var samples = new List<ChartSample>(bucketCount + 1);
                for (var bucket = firstBucket; bucket <= lastBucket; bucket++)
                {
                    from.Value = new DateTime(bucket * bucketTicks, DateTimeKind.Utc);
                    to.Value = new DateTime(
                        Math.Min((bucket + 1) * bucketTicks, endExclusiveTicks),
                        DateTimeKind.Utc);

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        continue;
                    }

                    // A bucket nothing was recorded in stays a hole rather than being reported as
                    // an outage, which is the difference between a monitor that was not running
                    // and a host that was not answering.
                    var recorded = reader.GetInt64(0);
                    if (recorded == 0)
                    {
                        continue;
                    }

                    samples.Add(ChartMath.BucketSample(
                        bucket, bucketDuration, rangeEndUtc, reader.GetInt64(1), reader.GetInt64(2)));
                }

                return samples;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        private static DbParameter Parameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
            return parameter;
        }
    }

    /// <summary>
    /// One host's series as a read of it left it, with what a later read needs to carry it on; see
    /// <see cref="ChartSeries.RefreshAsync"/>.
    /// </summary>
    /// <param name="Samples">The points, oldest first.</param>
    /// <param name="RangeStartUtc">The oldest instant the read covered.</param>
    /// <param name="RangeEndUtc">The newest.</param>
    /// <param name="BucketDuration">
    /// The width of the buckets the attempts were reduced into, or null when there were few enough
    /// of them to hand back as they were recorded.
    /// </param>
    public sealed record ChartSeriesRead(
        IReadOnlyList<ChartSample> Samples,
        DateTime RangeStartUtc,
        DateTime RangeEndUtc,
        TimeSpan? BucketDuration);
}
