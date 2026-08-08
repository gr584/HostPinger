namespace HostPinger.Core.Charting
{
    /// <summary>Scaling, tick, and layout math for the ping-vs-time line chart.</summary>
    public static class ChartMath
    {
        private static readonly TimeSpan[] TimeStepCandidates =
        [
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(3),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(12),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(2),
            TimeSpan.FromDays(7),
        ];

        /// <summary>Returns evenly spaced tick values from 0 to a "nice" value at or above maxValue.</summary>
        public static IReadOnlyList<double> NiceTicks(double maxValue, int maxTickCount = 6)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxTickCount, 2);
            if (maxValue <= 0)
            {
                return [0, 1];
            }

            var rawStep = maxValue / (maxTickCount - 1);
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            var step = (rawStep / magnitude) switch
            {
                <= 1 => magnitude,
                <= 2 => 2 * magnitude,
                <= 5 => 5 * magnitude,
                _ => 10 * magnitude,
            };

            var ticks = new List<double>();
            var top = Math.Ceiling(maxValue / step - 1e-9) * step;
            for (var value = 0d; value <= top + step / 2; value += step)
            {
                ticks.Add(value);
            }

            return ticks;
        }

        /// <summary>Returns tick timestamps aligned to a nice interval within [startUtc, endUtc].</summary>
        public static IReadOnlyList<DateTime> TimeTicks(DateTime startUtc, DateTime endUtc, int maxTickCount = 8)
        {
            if (endUtc <= startUtc)
            {
                return [];
            }

            var range = endUtc - startUtc;
            var step = TimeStepCandidates.FirstOrDefault(s => range.Ticks / s.Ticks < maxTickCount);
            if (step == default)
            {
                var weeks = (long)Math.Ceiling(range.Ticks / (double)(TimeSpan.TicksPerDay * 7) / (maxTickCount - 1));
                step = TimeSpan.FromTicks(TimeSpan.TicksPerDay * 7 * weeks);
            }

            var ticks = new List<DateTime>();
            var firstTick = (startUtc.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks;
            for (var t = firstTick; t <= endUtc.Ticks; t += step.Ticks)
            {
                ticks.Add(new DateTime(t, DateTimeKind.Utc));
            }

            return ticks;
        }

        /// <summary>
        /// Maps time-ordered samples into pixel space: polyline segments split where the host was
        /// down or where consecutive samples are further apart than maxGap, plus a baseline marker
        /// for every failed ping. Samples outside the range are skipped.
        /// </summary>
        public static ChartSeriesGeometry BuildGeometry(
            IEnumerable<ChartSample> samples,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            double maxY,
            double width,
            double height,
            TimeSpan? maxGap = null)
        {
            if (rangeEndUtc <= rangeStartUtc)
            {
                throw new ArgumentException("rangeEndUtc must be after rangeStartUtc.", nameof(rangeEndUtc));
            }

            if (maxY <= 0)
            {
                maxY = 1;
            }

            var rangeTicks = (double)(rangeEndUtc - rangeStartUtc).Ticks;
            var segments = new List<IReadOnlyList<ChartPoint>>();
            var downMarkers = new List<ChartPoint>();
            var current = new List<ChartPoint>();
            DateTime? lastTimestampUtc = null;

            foreach (var sample in samples)
            {
                if (sample.TimestampUtc < rangeStartUtc || sample.TimestampUtc > rangeEndUtc)
                {
                    continue;
                }

                var x = (sample.TimestampUtc - rangeStartUtc).Ticks / rangeTicks * width;
                if (sample.RoundtripMs is int value)
                {
                    if (lastTimestampUtc is DateTime previous && maxGap is TimeSpan gap
                        && sample.TimestampUtc - previous > gap && current.Count > 0)
                    {
                        segments.Add(current);
                        current = [];
                    }

                    var y = height - Math.Min(value, maxY) / maxY * height;
                    current.Add(new ChartPoint(x, y));
                    lastTimestampUtc = sample.TimestampUtc;
                }
                else
                {
                    downMarkers.Add(new ChartPoint(x, height));
                    if (current.Count > 0)
                    {
                        segments.Add(current);
                        current = [];
                    }

                    lastTimestampUtc = sample.TimestampUtc;
                }
            }

            if (current.Count > 0)
            {
                segments.Add(current);
            }

            return new ChartSeriesGeometry(segments, downMarkers);
        }

        /// <summary>The width of one <see cref="Downsample"/> bucket over the given range.</summary>
        public static TimeSpan BucketDuration(DateTime rangeStartUtc, DateTime rangeEndUtc, int bucketCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
            return (rangeEndUtc - rangeStartUtc) / bucketCount;
        }

        /// <summary>
        /// Reduces samples to at most bucketCount points by averaging them into equal-duration time
        /// buckets spanning [rangeStartUtc, rangeEndUtc]. A bucket holding only failed pings comes
        /// back null (down); a bucket holding no samples at all is left out, so a stretch where
        /// nothing was recorded stays a hole rather than being reported as an outage. Emitted
        /// samples carry their bucket's midpoint, which puts consecutive points exactly one
        /// <see cref="BucketDuration"/> apart and lets a caller tell the next bucket from a skipped
        /// one by time alone. Samples must be time-ordered; any outside the range are skipped.
        /// </summary>
        /// <remarks>
        /// Samples that already fit in bucketCount are handed back untouched, keeping their exact
        /// timestamps for the hover readout. Their spacing is then the ping cadence rather than the
        /// bucket width, so a caller splitting on a gap has to allow for both scales.
        /// </remarks>
        public static IReadOnlyList<ChartSample> Downsample(
            IReadOnlyList<ChartSample> samples,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int bucketCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
            if (rangeEndUtc <= rangeStartUtc)
            {
                throw new ArgumentException("rangeEndUtc must be after rangeStartUtc.", nameof(rangeEndUtc));
            }

            if (samples.Count <= bucketCount)
            {
                return samples;
            }

            var bucketTicks = (rangeEndUtc - rangeStartUtc).Ticks / (double)bucketCount;
            var result = new List<ChartSample>(bucketCount);

            // Time-ordered input means bucket indices only ever climb, so one running bucket is
            // enough: it is emitted when a sample lands past it, and once more at the end.
            var openBucket = -1;
            long valueSum = 0;
            var valueCount = 0;

            void EmitOpenBucket()
            {
                if (openBucket < 0)
                {
                    return;
                }

                var midpoint = new DateTime(
                    rangeStartUtc.Ticks + (long)((openBucket + 0.5) * bucketTicks),
                    DateTimeKind.Utc);
                result.Add(new ChartSample(midpoint, valueCount > 0 ? (int)(valueSum / valueCount) : null));
            }

            foreach (var sample in samples)
            {
                if (sample.TimestampUtc < rangeStartUtc || sample.TimestampUtc > rangeEndUtc)
                {
                    continue;
                }

                // The last bucket owns both its ends, so a sample sitting exactly on rangeEndUtc
                // joins it instead of opening a bucket past the end of the range.
                var bucket = Math.Min(
                    (int)((sample.TimestampUtc - rangeStartUtc).Ticks / bucketTicks),
                    bucketCount - 1);
                if (bucket != openBucket)
                {
                    EmitOpenBucket();
                    openBucket = bucket;
                    valueSum = 0;
                    valueCount = 0;
                }

                if (sample.RoundtripMs is int value)
                {
                    valueSum += value;
                    valueCount++;
                }
            }

            EmitOpenBucket();
            return result;
        }

        /// <summary>
        /// Returns the index of the sample closest in time to targetUtc, or -1 if the list is
        /// empty. Samples must be time-ordered.
        /// </summary>
        public static int NearestSampleIndex(IReadOnlyList<ChartSample> samples, DateTime targetUtc)
        {
            if (samples.Count == 0)
            {
                return -1;
            }

            var lo = 0;
            var hi = samples.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (samples[mid].TimestampUtc < targetUtc)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo > 0
                && Math.Abs(samples[lo - 1].TimestampUtc.Ticks - targetUtc.Ticks)
                    <= Math.Abs(samples[lo].TimestampUtc.Ticks - targetUtc.Ticks))
            {
                return lo - 1;
            }

            return lo;
        }

        /// <summary>
        /// Nudges label positions apart so adjacent labels are at least minGap apart, keeping them
        /// inside [minLimit, maxLimit]. Returns adjusted positions in the input order.
        /// </summary>
        public static IReadOnlyList<double> SpreadLabels(IReadOnlyList<double> positions, double minGap, double minLimit, double maxLimit)
        {
            var order = Enumerable.Range(0, positions.Count).OrderBy(i => positions[i]).ToArray();
            var adjusted = new double[positions.Count];

            var previous = double.NegativeInfinity;
            foreach (var index in order)
            {
                var value = Math.Max(Math.Max(positions[index], minLimit), previous + minGap);
                adjusted[index] = value;
                previous = value;
            }

            var next = double.PositiveInfinity;
            for (var rank = order.Length - 1; rank >= 0; rank--)
            {
                var index = order[rank];
                var value = Math.Min(Math.Min(adjusted[index], maxLimit), next - minGap);
                adjusted[index] = Math.Max(value, minLimit);
                next = adjusted[index];
            }

            return adjusted;
        }
    }
}
