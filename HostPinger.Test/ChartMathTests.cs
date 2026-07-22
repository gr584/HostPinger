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

            Assert.That(ChartMath.Downsample(samples, 10), Is.SameAs(samples));
        }

        [Test]
        public void Downsample_AveragesBuckets()
        {
            var samples = Enumerable.Range(0, 10)
                .Select(i => new ChartSample(Start.AddSeconds(i), i))
                .ToList();

            var result = ChartMath.Downsample(samples, 5);

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

            var result = ChartMath.Downsample(samples, 2);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].RoundtripMs, Is.EqualTo(11));
            Assert.That(result[1].RoundtripMs, Is.Null);
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
    }
}
