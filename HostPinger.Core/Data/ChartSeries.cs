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

        /// <param name="db">The context to read through.</param>
        /// <param name="hostId">The host to read.</param>
        /// <param name="rangeStartUtc">The oldest instant on the chart.</param>
        /// <param name="rangeEndUtc">The newest.</param>
        /// <param name="bucketCount">How many points the chart has room for.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        public static async Task<IReadOnlyList<ChartSample>> LoadAsync(
            HostPingerDbContext db,
            int hostId,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int bucketCount,
            CancellationToken cancellationToken = default)
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
            var queryStart = ChartMath.BucketStart(rangeStartUtc, bucketDuration);

            // Counted no further than one past what would fit, which is all the question below
            // asks: a range holding a year of attempts must not be walked end to end just to be
            // told it holds more than six hundred.
            var attempts = await db.PingAttempts.AsNoTracking()
                .Where(a => a.HostId == hostId && a.TimestampUtc >= queryStart && a.TimestampUtc <= rangeEndUtc)
                .Take(bucketCount + 1)
                .CountAsync(cancellationToken);

            // Few enough to draw as they were recorded. Downsample hands these back untouched so
            // the hover readout keeps the exact ping times rather than bucket midpoints, and it is
            // still the one place that rule is written down.
            if (attempts <= bucketCount)
            {
                var samples = await db.PingAttempts.AsNoTracking()
                    .Where(a => a.HostId == hostId && a.TimestampUtc >= queryStart && a.TimestampUtc <= rangeEndUtc)
                    .OrderBy(a => a.TimestampUtc)
                    .Select(a => new ChartSample(a.TimestampUtc, a.RoundtripMs))
                    .ToListAsync(cancellationToken);
                return ChartMath.Downsample(samples, rangeStartUtc, rangeEndUtc, bucketCount);
            }

            return await AggregateAsync(db, hostId, queryStart, rangeEndUtc, bucketDuration, bucketCount, cancellationToken);
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
}
