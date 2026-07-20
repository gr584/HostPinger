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
        /// down, plus a baseline marker for every failed ping. Samples outside the range are skipped.
        /// </summary>
        public static ChartSeriesGeometry BuildGeometry(
            IEnumerable<ChartSample> samples,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            double maxY,
            double width,
            double height)
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

            foreach (var sample in samples)
            {
                if (sample.TimestampUtc < rangeStartUtc || sample.TimestampUtc > rangeEndUtc)
                {
                    continue;
                }

                var x = (sample.TimestampUtc - rangeStartUtc).Ticks / rangeTicks * width;
                if (sample.RoundtripMs is int value)
                {
                    var y = height - Math.Min(value, maxY) / maxY * height;
                    current.Add(new ChartPoint(x, y));
                }
                else
                {
                    downMarkers.Add(new ChartPoint(x, height));
                    if (current.Count > 0)
                    {
                        segments.Add(current);
                        current = [];
                    }
                }
            }

            if (current.Count > 0)
            {
                segments.Add(current);
            }

            return new ChartSeriesGeometry(segments, downMarkers);
        }

        /// <summary>
        /// Reduces samples to at most maxPoints by averaging equal-size buckets. A bucket with no
        /// successful pings stays null (down). Samples must be time-ordered.
        /// </summary>
        public static IReadOnlyList<ChartSample> Downsample(IReadOnlyList<ChartSample> samples, int maxPoints)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);
            if (samples.Count <= maxPoints)
            {
                return samples;
            }

            var result = new List<ChartSample>(maxPoints);
            for (var bucket = 0; bucket < maxPoints; bucket++)
            {
                var startIndex = (int)((long)bucket * samples.Count / maxPoints);
                var endIndex = (int)((long)(bucket + 1) * samples.Count / maxPoints);
                if (endIndex <= startIndex)
                {
                    continue;
                }

                long timestampTicksSum = 0;
                long valueSum = 0;
                var valueCount = 0;
                for (var i = startIndex; i < endIndex; i++)
                {
                    timestampTicksSum += samples[i].TimestampUtc.Ticks - samples[startIndex].TimestampUtc.Ticks;
                    if (samples[i].RoundtripMs is int value)
                    {
                        valueSum += value;
                        valueCount++;
                    }
                }

                var timestamp = new DateTime(
                    samples[startIndex].TimestampUtc.Ticks + timestampTicksSum / (endIndex - startIndex),
                    DateTimeKind.Utc);
                int? average = valueCount > 0 ? (int)(valueSum / valueCount) : null;
                result.Add(new ChartSample(timestamp, average));
            }

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
