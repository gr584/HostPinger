using HostPinger.Core.Data;

namespace HostPinger.Test
{
    /// <summary>
    /// How an attempt's time becomes the Unix millisecond stored for it and back, and the bounds a
    /// query rounds up rather than down.
    /// </summary>
    public class StoredTimeTests
    {
        private static readonly DateTime Second = new(2026, 10, 10, 18, 1, 10, DateTimeKind.Utc);

        private static IEnumerable<TestCaseData> Times()
        {
            yield return new TestCaseData(Second, 1_791_655_270_000L).SetName("a whole second");
            yield return new TestCaseData(Second.AddTicks(7_688_978), 1_791_655_270_768L).SetName("a fraction, rounded down");
            yield return new TestCaseData(Second.AddTicks(9_999), 1_791_655_270_000L).SetName("just short of the next millisecond");
            yield return new TestCaseData(DateTime.UnixEpoch, 0L).SetName("the epoch");
            yield return new TestCaseData(DateTime.UnixEpoch.AddTicks(-1), -1L).SetName("just before the epoch, down and away from zero");
            yield return new TestCaseData(DateTime.UnixEpoch.AddTicks(-10_000), -1L).SetName("a whole millisecond before the epoch");
            yield return new TestCaseData(DateTime.UnixEpoch.AddTicks(-10_001), -2L).SetName("just past it");
            yield return new TestCaseData(DateTime.MinValue, -62_135_596_800_000L).SetName("the earliest DateTime");
            yield return new TestCaseData(DateTime.MaxValue, 253_402_300_799_999L).SetName("the latest DateTime");
        }

        [TestCaseSource(nameof(Times))]
        public void ToMilliseconds_RoundsDownToTheMillisecond(DateTime time, long expected) =>
            Assert.That(StoredTime.ToMilliseconds(time), Is.EqualTo(expected));

        /// <summary>
        /// Reading back gives the start of the millisecond, as UTC, and that is a time storing again
        /// keeps exactly: which is what a round stamping its time cut already relies on.
        /// </summary>
        [TestCaseSource(nameof(Times))]
        public void Truncate_IsTheStartOfTheMillisecondAndSurvivesStoring(DateTime time, long expected)
        {
            var truncated = StoredTime.Truncate(time);

            Assert.Multiple(() =>
            {
                Assert.That(truncated, Is.EqualTo(StoredTime.FromMilliseconds(expected)));
                Assert.That(truncated, Is.LessThanOrEqualTo(time));
                Assert.That(time - truncated, Is.LessThan(TimeSpan.FromMilliseconds(1)));
                Assert.That(truncated.Kind, Is.EqualTo(DateTimeKind.Utc));
                Assert.That(StoredTime.FromMilliseconds(StoredTime.ToMilliseconds(truncated)), Is.EqualTo(truncated));
            });
        }

        /// <summary>
        /// A lower bound is rounded up to the next millisecond unless it is on one already, which is
        /// what keeps "at or after" exact for the times the column holds.
        /// </summary>
        [Test]
        public void CeilingMilliseconds_RoundsUpOnlyWhatFallsInsideAMillisecond()
        {
            Assert.Multiple(() =>
            {
                Assert.That(StoredTime.CeilingMilliseconds(Second), Is.EqualTo(1_791_655_270_000L));
                Assert.That(StoredTime.CeilingMilliseconds(Second.AddTicks(1)), Is.EqualTo(1_791_655_270_001L));
                Assert.That(StoredTime.CeilingMilliseconds(Second.AddTicks(9_999)), Is.EqualTo(1_791_655_270_001L));
                Assert.That(StoredTime.CeilingMilliseconds(DateTime.UnixEpoch.AddTicks(-1)), Is.Zero);
                Assert.That(StoredTime.CeilingMilliseconds(DateTime.MaxValue), Is.EqualTo(253_402_300_800_000L),
                    "past the last DateTime, which a bound can be without being read back");
                Assert.That(StoredTime.Ceiling(Second.AddTicks(1)), Is.EqualTo(Second.AddMilliseconds(1)));
            });
        }
    }
}
