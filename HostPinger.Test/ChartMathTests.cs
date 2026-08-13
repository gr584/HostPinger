using HostPinger.Core.Charting;

namespace HostPinger.Test
{
    public class ChartMathTests
    {
        private static readonly DateTime Start = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        [Test]
        public void NiceTicks_RoundsUpToNiceStep()
        {
            Assert.That(ChartMath.NiceTicks(97, 6), Is.EqualTo(new[] { 0d, 20, 40, 60, 80, 100 }));
        }

        [Test]
        public void NiceTicks_SmallMaxUsesUnitStep()
        {
            Assert.That(ChartMath.NiceTicks(4, 6), Is.EqualTo(new[] { 0d, 1, 2, 3, 4 }));
        }

        [Test]
        public void NiceTicks_NonPositiveMaxFallsBackToUnitRange()
        {
            Assert.That(ChartMath.NiceTicks(0), Is.EqualTo(new[] { 0d, 1 }));
        }

        [Test]
        public void TimeTicks_AlignsToNiceBoundaries()
        {
            var start = new DateTime(2026, 1, 1, 10, 7, 0, DateTimeKind.Utc);
            var end = new DateTime(2026, 1, 1, 11, 7, 0, DateTimeKind.Utc);

            var ticks = ChartMath.TimeTicks(start, end, 8);

            Assert.That(ticks, Is.EqualTo(new[]
            {
                new DateTime(2026, 1, 1, 10, 15, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 45, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            }));
        }

        [Test]
        public void TimeTicks_EmptyWhenRangeInverted()
        {
            Assert.That(ChartMath.TimeTicks(Start, Start), Is.Empty);
        }

        [Test]
        public void BuildGeometry_MapsSamplesToPixels()
        {
            var samples = new[] { new ChartSample(Start.AddMinutes(5), 50) };

            var geometry = ChartMath.BuildGeometry(samples, Start, Start.AddMinutes(10), 100, 100, 100);

            Assert.That(geometry.Segments, Has.Count.EqualTo(1));
            var point = geometry.Segments[0][0];
            Assert.That(point.X, Is.EqualTo(50).Within(1e-9));
            Assert.That(point.Y, Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void BuildGeometry_SplitsSegmentsWhereHostWasDown()
        {
            var samples = new[]
            {
                new ChartSample(Start.AddMinutes(1), 10),
                new ChartSample(Start.AddMinutes(2), null),
                new ChartSample(Start.AddMinutes(3), 20),
            };

            var geometry = ChartMath.BuildGeometry(samples, Start, Start.AddMinutes(10), 100, 100, 100);

            Assert.That(geometry.Segments, Has.Count.EqualTo(2));
            Assert.That(geometry.DownMarkers, Has.Count.EqualTo(1));
            Assert.That(geometry.DownMarkers[0].Y, Is.EqualTo(100));
        }

        [Test]
        public void BuildGeometry_SplitsSegmentsWhereGapExceedsMaxGap()
        {
            var samples = new[]
            {
                new ChartSample(Start.AddMinutes(1), 10),
                new ChartSample(Start.AddMinutes(2), 20),
                new ChartSample(Start.AddMinutes(9), 30),
            };

            var geometry = ChartMath.BuildGeometry(
                samples, Start, Start.AddMinutes(10), 100, 100, 100, maxGap: TimeSpan.FromMinutes(5));

            Assert.That(geometry.Segments, Has.Count.EqualTo(2));
            Assert.That(geometry.Segments[0], Has.Count.EqualTo(2));
            Assert.That(geometry.Segments[1], Has.Count.EqualTo(1));
            Assert.That(geometry.DownMarkers, Is.Empty);
        }

        [Test]
        public void BuildGeometry_WithoutMaxGapConnectsAcrossLargeGaps()
        {
            var samples = new[]
            {
                new ChartSample(Start.AddMinutes(1), 10),
                new ChartSample(Start.AddMinutes(9), 30),
            };

            var geometry = ChartMath.BuildGeometry(samples, Start, Start.AddMinutes(10), 100, 100, 100);

            Assert.That(geometry.Segments, Has.Count.EqualTo(1));
            Assert.That(geometry.Segments[0], Has.Count.EqualTo(2));
        }

        [Test]
        public void BuildGeometry_SkipsSamplesOutsideRange()
        {
            var samples = new[]
            {
                new ChartSample(Start.AddMinutes(-1), 10),
                new ChartSample(Start.AddMinutes(5), 20),
                new ChartSample(Start.AddMinutes(11), 30),
            };

            var geometry = ChartMath.BuildGeometry(samples, Start, Start.AddMinutes(10), 100, 100, 100);

            Assert.That(geometry.Segments, Has.Count.EqualTo(1));
            Assert.That(geometry.Segments[0], Has.Count.EqualTo(1));
        }

        [Test]
        public void Downsample_ReturnsInputWhenUnderLimit()
        {
            var samples = Enumerable.Range(0, 5)
                .Select(i => new ChartSample(Start.AddSeconds(i), i))
                .ToList();

            Assert.That(ChartMath.Downsample(samples, Start, Start.AddSeconds(10), 10), Is.SameAs(samples));
        }

        [Test]
        public void Downsample_AveragesBuckets()
        {
            var samples = Enumerable.Range(0, 10)
                .Select(i => new ChartSample(Start.AddSeconds(i), i))
                .ToList();

            var result = ChartMath.Downsample(samples, Start, Start.AddSeconds(10), 5);

            Assert.That(result, Has.Count.EqualTo(5));
            Assert.That(result.Select(s => s.RoundtripMs), Is.EqualTo(new int?[] { 0, 2, 4, 6, 8 }));
        }

        [Test]
        public void Downsample_BucketWithNoRepliesStaysDown()
        {
            var samples = new[]
            {
                new ChartSample(Start, 10),
                new ChartSample(Start.AddSeconds(1), 12),
                new ChartSample(Start.AddSeconds(2), null),
                new ChartSample(Start.AddSeconds(3), null),
            };

            var result = ChartMath.Downsample(samples, Start, Start.AddSeconds(4), 2);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].RoundtripMs, Is.EqualTo(11));
            Assert.That(result[1].RoundtripMs, Is.Null);
        }

        [Test]
        public void Downsample_TimestampsBucketsAtTheirMidpoints()
        {
            var samples = Enumerable.Range(0, 10)
                .Select(i => new ChartSample(Start.AddSeconds(i), i))
                .ToList();

            var result = ChartMath.Downsample(samples, Start, Start.AddSeconds(10), 5);

            Assert.That(
                result.Select(s => s.TimestampUtc),
                Is.EqualTo(new[] { 1, 3, 5, 7, 9 }.Select(s => Start.AddSeconds(s))));
        }

        [Test]
        public void Downsample_OmitsBucketsWithNoSamples()
        {
            // Twelve samples clustered into the first and last minute of a ten-bucket range: the
            // eight empty minutes in between must not come back as down, only as absent.
            var samples = Enumerable.Range(0, 6)
                .Select(i => new ChartSample(Start.AddSeconds(i * 5), 10))
                .Concat(Enumerable.Range(0, 6)
                    .Select(i => new ChartSample(Start.AddMinutes(9).AddSeconds(i * 5), 20)))
                .ToList();

            var result = ChartMath.Downsample(samples, Start, Start.AddMinutes(10), 10);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.Select(s => s.RoundtripMs), Is.EqualTo(new int?[] { 10, 20 }));
            Assert.That(
                result.Select(s => s.TimestampUtc),
                Is.EqualTo(new[] { Start.AddSeconds(30), Start.AddMinutes(9).AddSeconds(30) }));
        }

        [Test]
        public void Downsample_KeepsAdjacentBucketsInsideTheChartsGapThreshold()
        {
            // The regression this guards: consecutive buckets are one bucket apart, so the 1.5
            // bucket threshold LineChart splits on must join them and break only where the data
            // itself stops — here a middle stretch with no samples at all.
            var end = Start.AddMinutes(10);
            const int bucketCount = 20;
            var samples = Enumerable.Range(0, 60)
                .Where(i => i < 20 || i >= 40)
                .Select(i => new ChartSample(Start.AddSeconds(i * 10), 10))
                .ToList();

            var reduced = ChartMath.Downsample(samples, Start, end, bucketCount);
            var maxGap = ChartMath.BucketDuration(Start, end, bucketCount) * 1.5;
            var geometry = ChartMath.BuildGeometry(reduced, Start, end, 100, 100, 100, maxGap);

            Assert.That(reduced, Has.Count.EqualTo(14));
            Assert.That(geometry.Segments, Has.Count.EqualTo(2));
            Assert.That(geometry.Segments[0], Has.Count.EqualTo(7));
            Assert.That(geometry.Segments[1], Has.Count.EqualTo(7));
            Assert.That(geometry.DownMarkers, Is.Empty);
        }

        [Test]
        public void Downsample_BucketsOnAGridFixedInTimeRatherThanOnTheRangeStart()
        {
            // A range starting three quarters of the way through a minute, bucketed at one bucket a
            // minute: the buckets are the minutes themselves, not five minutes measured off 10:00:45.
            var rangeStart = Start.AddSeconds(45);
            var rangeEnd = rangeStart.AddMinutes(5);
            var samples = Enumerable.Range(0, 70)
                .Select(i => new ChartSample(Start.AddSeconds(i * 5), i * 5 < 45 ? 10 : 20))
                .ToList();

            var result = ChartMath.Downsample(samples, rangeStart, rangeEnd, 5);

            Assert.Multiple(() =>
            {
                Assert.That(
                    result.Select(s => s.TimestampUtc),
                    Is.EqualTo(new[] { 30, 90, 150, 210, 270, 330 }.Select(s => Start.AddSeconds(s))));

                // The first bucket is the whole of the 10:00 minute, three quarters of which came
                // before the range: averaging only the part inside it would read 20 like the rest.
                Assert.That(result.Select(s => s.RoundtripMs), Is.EqualTo(new int?[] { 12, 20, 20, 20, 20, 20 }));
            });
        }

        [Test]
        public void Downsample_ReportsTheBucketStillFillingAtTheRangeEnd()
        {
            // The range ends a third of the way into the 10:04 bucket, whose midpoint is still to
            // come: it has to be reported at the range end, or what is arriving now would be missing
            // from the near edge of a live chart until the bucket was half over.
            var rangeEnd = Start.AddMinutes(4).AddSeconds(20);
            var rangeStart = rangeEnd.AddMinutes(-5);
            var samples = Enumerable.Range(0, 53)
                .Select(i => new ChartSample(Start.AddSeconds(i * 5), 100 + i))
                .ToList();

            var result = ChartMath.Downsample(samples, rangeStart, rangeEnd, 5);

            Assert.Multiple(() =>
            {
                Assert.That(result[^1].TimestampUtc, Is.EqualTo(rangeEnd));

                // 10:04:00, :05, :10, :15 and :20 — the part of the bucket that has happened.
                Assert.That(result[^1].RoundtripMs, Is.EqualTo(150));
                Assert.That(result[^2].TimestampUtc, Is.EqualTo(Start.AddMinutes(3).AddSeconds(30)));
            });
        }

        [Test]
        public void Downsample_LeavesBucketsAloneAsTheRangeSlidesOn()
        {
            // The regression this guards: a live chart reloads every few seconds over a range that
            // has moved on by that much, and everything but the bucket the present is in has to come
            // back exactly as it was. Buckets measured off the range start instead of a fixed grid
            // re-average on every refresh, which redraws history that never changed.
            const int bucketCount = 60;
            var window = TimeSpan.FromHours(1);
            var now = Start.AddHours(2).AddSeconds(7);
            var samples = Enumerable.Range(0, 2000)
                .Select(i => new ChartSample(now.AddSeconds(-5 * i), i % 37))
                .Reverse()
                .ToList();

            var bucket = ChartMath.BucketDuration(now - window, now, bucketCount);
            IReadOnlyList<ChartSample> Reduce(DateTime end) => ChartMath.Downsample(
                samples.Where(s => s.TimestampUtc >= ChartMath.BucketStart(end - window, bucket)).ToList(),
                end - window,
                end,
                bucketCount);

            var before = Reduce(now);
            var after = Reduce(now.AddSeconds(5));

            // Every bucket the two loads have in common, which is all of them bar the one filling at
            // either end of the pair of ranges.
            var overlapStart = now.AddSeconds(5) - window;
            bool InOverlap(ChartSample s) => s.TimestampUtc > overlapStart && s.TimestampUtc < now;

            Assert.That(before.Where(InOverlap).ToList(), Has.Count.EqualTo(bucketCount));
            Assert.That(after.Where(InOverlap), Is.EqualTo(before.Where(InOverlap)));
        }

        [Test]
        public void BucketStart_SnapsBackToTheEnclosingBucket()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    ChartMath.BucketStart(Start.AddSeconds(95), TimeSpan.FromMinutes(1)),
                    Is.EqualTo(Start.AddMinutes(1)));
                Assert.That(
                    ChartMath.BucketStart(Start.AddMinutes(1), TimeSpan.FromMinutes(1)),
                    Is.EqualTo(Start.AddMinutes(1)));
            });
        }

        [Test]
        public void NearestSampleIndex_FindsClosestSample()
        {
            var samples = new[]
            {
                new ChartSample(Start, 1),
                new ChartSample(Start.AddMinutes(10), 2),
                new ChartSample(Start.AddMinutes(20), 3),
            };

            Assert.Multiple(() =>
            {
                Assert.That(ChartMath.NearestSampleIndex(samples, Start.AddMinutes(12)), Is.EqualTo(1));
                Assert.That(ChartMath.NearestSampleIndex(samples, Start.AddMinutes(16)), Is.EqualTo(2));
                Assert.That(ChartMath.NearestSampleIndex(samples, Start.AddMinutes(99)), Is.EqualTo(2));
                Assert.That(ChartMath.NearestSampleIndex(samples, Start.AddMinutes(-5)), Is.EqualTo(0));
                Assert.That(ChartMath.NearestSampleIndex([], Start), Is.EqualTo(-1));
            });
        }

        [Test]
        public void SpreadLabels_SeparatesOverlappingLabels()
        {
            var result = ChartMath.SpreadLabels([50, 52, 54], 10, 0, 300);

            Assert.That(result, Is.EqualTo(new[] { 50d, 60, 70 }));
        }

        [Test]
        public void SpreadLabels_RespectsUpperLimit()
        {
            var result = ChartMath.SpreadLabels([95, 98], 10, 0, 100);

            Assert.That(result, Is.EqualTo(new[] { 90d, 100 }));
        }

        [Test]
        public void SelectionFromPixels_MapsThePlotOntoTheRange()
        {
            var selection = ChartMath.SelectionFromPixels(25, 50, 100, Start, Start.AddMinutes(60), 8, 6);

            Assert.Multiple(() =>
            {
                Assert.That(selection!.StartUtc, Is.EqualTo(Start.AddMinutes(15)));
                Assert.That(selection.EndUtc, Is.EqualTo(Start.AddMinutes(30)));
                Assert.That(selection.StartUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            });
        }

        /// <summary>Dragging right to left picks out the same stretch of time as dragging left to right.</summary>
        [Test]
        public void SelectionFromPixels_OrdersTheEndsOfTheDrag()
        {
            var forwards = ChartMath.SelectionFromPixels(25, 50, 100, Start, Start.AddMinutes(60), 8, 6);
            var backwards = ChartMath.SelectionFromPixels(50, 25, 100, Start, Start.AddMinutes(60), 8, 6);

            Assert.That(backwards, Is.EqualTo(forwards));
        }

        [Test]
        public void SelectionFromPixels_ClampsADragThatRanOffThePlot()
        {
            var selection = ChartMath.SelectionFromPixels(-40, 160, 100, Start, Start.AddMinutes(60), 8, 6);

            Assert.Multiple(() =>
            {
                Assert.That(selection!.StartUtc, Is.EqualTo(Start));
                Assert.That(selection.EndUtc, Is.EqualTo(Start.AddMinutes(60)));
            });
        }

        /// <summary>
        /// Ending at the right-hand edge is what tells a width ending "now" apart from a fixed slice
        /// of the past, and it only has to be hit within a tolerance because it is aimed at by eye.
        /// </summary>
        [Test]
        public void SelectionFromPixels_FlagsADragThatReachesTheEnd()
        {
            var end = Start.AddMinutes(60);

            Assert.Multiple(() =>
            {
                Assert.That(ChartMath.SelectionFromPixels(20, 100, 100, Start, end, 8, 6)!.ReachesRangeEnd, Is.True);
                Assert.That(ChartMath.SelectionFromPixels(20, 96, 100, Start, end, 8, 6)!.ReachesRangeEnd, Is.True,
                    "a drag landing inside the tolerance should still count as reaching the end");
                Assert.That(ChartMath.SelectionFromPixels(20, 90, 100, Start, end, 8, 6)!.ReachesRangeEnd, Is.False);
            });
        }

        [Test]
        public void SelectionFromPixels_IgnoresADragTooShortToBeOne()
        {
            var end = Start.AddMinutes(60);

            Assert.Multiple(() =>
            {
                Assert.That(ChartMath.SelectionFromPixels(50, 50, 100, Start, end, 8, 6), Is.Null,
                    "a click selects nothing");
                Assert.That(ChartMath.SelectionFromPixels(50, 55, 100, Start, end, 8, 6), Is.Null);
                Assert.That(ChartMath.SelectionFromPixels(50, 58, 100, Start, end, 8, 6), Is.Not.Null);
            });
        }

        [Test]
        public void SelectionFromPixels_RefusesADegenerateRangeOrPlot()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ChartMath.SelectionFromPixels(10, 90, 0, Start, Start.AddMinutes(60), 8, 6), Is.Null,
                    "an unmeasured plot has no pixels to map");
                Assert.That(ChartMath.SelectionFromPixels(10, 90, 100, Start, Start, 8, 6), Is.Null);
                Assert.That(ChartMath.SelectionFromPixels(10, 90, 100, Start, Start.AddMinutes(-1), 8, 6), Is.Null);
            });
        }

        /// <summary>
        /// The selected range has to be something the chart can draw. A slice of a range already so
        /// short that both ends of the drag land on the same tick is not: every scale built from it
        /// would divide by a zero-width range.
        /// </summary>
        [Test]
        public void SelectionFromPixels_RefusesARangeThatRoundsToNothing()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ChartMath.SelectionFromPixels(0, 10, 100, Start, Start.AddTicks(5), 8, 6), Is.Null);
                Assert.That(ChartMath.SelectionFromPixels(0, 100, 100, Start, Start.AddTicks(5), 8, 6), Is.Not.Null,
                    "a short range is still drawable as long as the drag covers more than none of it");
            });
        }
    }
}
