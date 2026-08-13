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

        /// <summary>
        /// Turns the two ends of a drag across the plot into the range of time it covers, or null
        /// if it covers too little to be one. The ends are ordered, so dragging right to left picks
        /// the same range as dragging left to right, and clamped to the plot, so a drag that runs
        /// off the edge stops at it rather than selecting time that was never on screen.
        /// </summary>
        /// <param name="fromX">Where the drag started, in pixels from the left edge of the plot.</param>
        /// <param name="toX">Where it ended, in the same space.</param>
        /// <param name="minimumWidthPx">
        /// Below this the drag is treated as a click that wobbled and nothing is selected. It is
        /// also what makes a drag abandonable: come back to where you started and let go.
        /// </param>
        /// <param name="edgeTolerancePx">
        /// How near the right-hand edge still counts as reaching it. A drag meant to end at "now"
        /// is aimed at that edge by eye and lands a pixel or two short of it as often as not.
        /// </param>
        public static ChartSelection? SelectionFromPixels(
            double fromX,
            double toX,
            double plotWidth,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            double minimumWidthPx,
            double edgeTolerancePx)
        {
            if (plotWidth <= 0 || rangeEndUtc <= rangeStartUtc)
            {
                return null;
            }

            var lo = Math.Clamp(Math.Min(fromX, toX), 0, plotWidth);
            var hi = Math.Clamp(Math.Max(fromX, toX), 0, plotWidth);
            if (hi - lo < minimumWidthPx)
            {
                return null;
            }

            var rangeTicks = (rangeEndUtc - rangeStartUtc).Ticks;
            var startUtc = rangeStartUtc.AddTicks((long)(lo / plotWidth * rangeTicks));
            var endUtc = rangeStartUtc.AddTicks((long)(hi / plotWidth * rangeTicks));

            // A range narrow enough to round to nothing would be a range the chart cannot draw.
            return endUtc > startUtc
                ? new ChartSelection(startUtc, endUtc, hi >= plotWidth - edgeTolerancePx)
                : null;
        }

        /// <summary>The width of one <see cref="Downsample"/> bucket over the given range.</summary>
        public static TimeSpan BucketDuration(DateTime rangeStartUtc, DateTime rangeEndUtc, int bucketCount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
            return TimeSpan.FromTicks(Math.Max(1, (rangeEndUtc - rangeStartUtc).Ticks / bucketCount));
        }

        /// <summary>
        /// The start of the bucket <paramref name="instantUtc"/> falls in, on the same grid
        /// <see cref="Downsample"/> buckets onto. Feeding a bucketed series from here rather than
        /// from the range start is what makes its oldest bucket an average over the whole of itself
        /// rather than over the part of it the range has not yet slid past.
        /// </summary>
        public static DateTime BucketStart(DateTime instantUtc, TimeSpan bucketDuration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(bucketDuration.Ticks, 1);
            return new DateTime(instantUtc.Ticks / bucketDuration.Ticks * bucketDuration.Ticks, DateTimeKind.Utc);
        }

        /// <summary>
        /// Reduces samples to about bucketCount points by averaging them into
        /// <see cref="BucketDuration"/>-wide time buckets covering [rangeStartUtc, rangeEndUtc]. A
        /// bucket holding only failed pings comes back null (down); a bucket holding no samples at
        /// all is left out, so a stretch where nothing was recorded stays a hole rather than being
        /// reported as an outage. Emitted samples carry their bucket's midpoint, which puts
        /// consecutive points one <see cref="BucketDuration"/> apart — the bucket still filling can
        /// fall closer, never further — and lets a caller tell the next bucket from a skipped one by
        /// time alone. Samples must be time-ordered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The buckets are cells of a grid fixed in absolute time — a whole number of bucket widths
        /// from the start of the calendar — rather than measured off rangeStartUtc. That is what
        /// keeps a chart that follows the clock still: were they measured off the range, every
        /// bucket boundary would move with it, and a refresh five seconds later would re-average
        /// every bucket on screen and redraw history that had not changed. On the grid, a bucket
        /// covers the same absolute stretch of time whenever it is asked for, so all a refresh can
        /// do is fill the bucket the present is in and drop the one that has fallen off the far end;
        /// everything between keeps its value and only slides along the axis.
        /// </para>
        /// <para>
        /// The consequence for the caller is that both ends of the range generally fall inside a
        /// bucket rather than on a boundary, and that the result can hold one more point than
        /// bucketCount. The bucket rangeEndUtc falls in is still filling, and its midpoint may be an
        /// instant that has not arrived, so it reports at rangeEndUtc instead: on a live chart what
        /// is coming in now belongs at the leading edge, not off the end of it. The bucket
        /// rangeStartUtc falls in is averaged over all of itself, which needs the caller to pass the
        /// samples from <see cref="BucketStart"/> onwards rather than from the range; samples
        /// belonging to buckets wholly before the range are skipped either way. That bucket's
        /// midpoint may land before rangeStartUtc, where a chart clipping to the range does not draw
        /// it — half a bucket of the oldest end, which is half a pixel at one bucket per pixel.
        /// </para>
        /// <para>
        /// Samples that already fit in bucketCount are handed back untouched, keeping their exact
        /// timestamps for the hover readout. Their spacing is then the ping cadence rather than the
        /// bucket width, so a caller splitting on a gap has to allow for both scales.
        /// </para>
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

            var bucketTicks = BucketDuration(rangeStartUtc, rangeEndUtc, bucketCount).Ticks;
            var firstBucket = rangeStartUtc.Ticks / bucketTicks;
            var result = new List<ChartSample>(bucketCount + 1);

            // Time-ordered input means bucket indices only ever climb, so one running bucket is
            // enough: it is emitted when a sample lands past it, and once more at the end. Indices
            // are grid cells counted from the start of the calendar, so they are always positive
            // and -1 can stand for "nothing open yet".
            var openBucket = -1L;
            long valueSum = 0;
            var valueCount = 0;

            void EmitOpenBucket()
            {
                if (openBucket < 0)
                {
                    return;
                }

                // The bucket the range ends in is the one still filling, and its midpoint may be an
                // instant that has not arrived; it reports at the range end rather than in the future.
                var midpoint = Math.Min(openBucket * bucketTicks + bucketTicks / 2, rangeEndUtc.Ticks);
                result.Add(new ChartSample(
                    new DateTime(midpoint, DateTimeKind.Utc),
                    valueCount > 0 ? (int)(valueSum / valueCount) : null));
            }

            foreach (var sample in samples)
            {
                if (sample.TimestampUtc > rangeEndUtc)
                {
                    continue;
                }

                var bucket = sample.TimestampUtc.Ticks / bucketTicks;
                if (bucket < firstBucket)
                {
                    continue;
                }

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
