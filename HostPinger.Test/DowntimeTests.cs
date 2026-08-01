using HostPinger.Core.Data;

namespace HostPinger.Test
{
    public class DowntimeTests
    {
        private static readonly DateTime Start = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void DurationAt_MeasuresToThePingThatAnsweredAgain()
        {
            var downtime = new Downtime(Start, Start.AddMinutes(7));

            Assert.That(downtime.DurationAt(Start.AddHours(3)), Is.EqualTo(TimeSpan.FromMinutes(7)));
        }

        [Test]
        public void DurationAt_MeasuresToNowWhileOngoing()
        {
            var downtime = new Downtime(Start, null);

            Assert.Multiple(() =>
            {
                Assert.That(downtime.IsOngoing, Is.True);
                Assert.That(downtime.DurationAt(Start.AddMinutes(90)), Is.EqualTo(TimeSpan.FromMinutes(90)));
            });
        }

        [TestCase(0, "0s")]
        [TestCase(1, "1s")]
        [TestCase(59, "59s")]
        [TestCase(60, "1m")]
        [TestCase(200, "3m 20s")]
        [TestCase(3600, "1h")]
        [TestCase(7500, "2h 5m")]
        [TestCase(86400, "1d")]
        [TestCase(367200, "4d 6h")]
        public void FormatDuration_UsesTheTwoLargestNonZeroUnits(int seconds, string expected)
        {
            Assert.That(Downtime.FormatDuration(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
        }

        [Test]
        public void FormatDuration_DropsFractionsOfASecond()
        {
            Assert.That(Downtime.FormatDuration(TimeSpan.FromMilliseconds(1900)), Is.EqualTo("1s"));
        }

        /// <summary>
        /// A clock that moves backwards — an NTP correction, say — must not render as a negative
        /// downtime.
        /// </summary>
        [Test]
        public void FormatDuration_ClampsNegativeSpansToZero()
        {
            Assert.That(Downtime.FormatDuration(TimeSpan.FromSeconds(-30)), Is.EqualTo("0s"));
        }
    }
}
