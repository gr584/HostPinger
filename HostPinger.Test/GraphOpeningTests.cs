using HostPinger.Core.Charting;

namespace HostPinger.Test
{
    public class GraphOpeningTests
    {
        private static readonly DateTime Start = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// What the Hosts page's Last downtime column asks for once an outage is over: the outage
        /// in the middle half of the chart, with a quarter of the range either side of it as
        /// context.
        /// </summary>
        [Test]
        public void Around_PutsTheStretchInTheMiddleOfAPeriodTwiceAsWide()
        {
            var period = GraphOpening.Around(Start, Start.AddMinutes(20));

            Assert.Multiple(() =>
            {
                Assert.That(period.StartUtc, Is.EqualTo(Start.AddMinutes(-10)));
                Assert.That(period.EndUtc, Is.EqualTo(Start.AddMinutes(30)));
            });
        }

        /// <summary>
        /// A blip of a few seconds doubled is still a few seconds. It is widened about its own
        /// middle instead, so it stays centred but arrives at a window with pings either side of it.
        /// </summary>
        [Test]
        public void Around_WidensAStretchTooShortToRead()
        {
            var period = GraphOpening.Around(Start, Start.AddSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(period.EndUtc - period.StartUtc, Is.EqualTo(TimeSpan.FromMinutes(1)));
                Assert.That(period.StartUtc, Is.EqualTo(Start.AddSeconds(-29)));
            });
        }

        /// <summary>
        /// A clock that stepped backwards over the outage measures it as ending before it began.
        /// The period still has to come out the right way round, or the chart is asked to draw
        /// something it cannot.
        /// </summary>
        [Test]
        public void Around_OrdersAPeriodBuiltFromABackwardsStretch()
        {
            var period = GraphOpening.Around(Start, Start.AddSeconds(-10));

            Assert.Multiple(() =>
            {
                Assert.That(period.EndUtc, Is.GreaterThan(period.StartUtc));
                Assert.That(period.EndUtc - period.StartUtc, Is.EqualTo(TimeSpan.FromMinutes(1)));
            });
        }

        /// <summary>
        /// What the same column asks for while an outage is still running: a window one and a half
        /// times as long as it, which is the outage in the last two thirds of the chart and what
        /// led up to it in the first third.
        /// </summary>
        [Test]
        public void Following_LeavesTheStretchFillingTheLastTwoThirds()
        {
            var window = GraphOpening.Following(TimeSpan.FromHours(1));

            Assert.That(window.Width, Is.EqualTo(TimeSpan.FromMinutes(90)));
        }

        [Test]
        public void Following_WidensAStretchTooShortToRead()
        {
            var window = GraphOpening.Following(TimeSpan.FromSeconds(2));

            Assert.That(window.Width, Is.EqualTo(TimeSpan.FromMinutes(1)));
        }

        /// <summary>
        /// A host that has been down for years is not a reason to ask the chart for a window of
        /// years, and the width has to stay one that "now minus it" is an instant the calendar has.
        /// </summary>
        [Test]
        public void Following_CapsAStretchLongerThanTheChartWillDraw()
        {
            var window = GraphOpening.Following(TimeSpan.FromDays(4000));

            Assert.That(window.Width, Is.EqualTo(TimeSpan.FromDays(365)));
        }

        /// <summary>
        /// The address an opening is written into is the whole of what it takes to read it back,
        /// which is what makes a link to a particular outage worth sending to someone.
        /// </summary>
        [Test]
        public void QueryString_IsReadBackByParse()
        {
            var period = GraphOpening.Around(Start, Start.AddMinutes(20));
            var window = GraphOpening.Following(TimeSpan.FromHours(1));

            Assert.Multiple(() =>
            {
                Assert.That(period.QueryString, Is.EqualTo("from=2026-07-31T11:50:00Z&to=2026-07-31T12:30:00Z"));
                Assert.That(
                    GraphOpening.Parse("2026-07-31T11:50:00Z", "2026-07-31T12:30:00Z", null),
                    Is.EqualTo(period));

                Assert.That(window.QueryString, Is.EqualTo("window=5400"));
                Assert.That(GraphOpening.Parse(null, null, "5400"), Is.EqualTo(window));
            });
        }

        [Test]
        public void Parse_ReadsBothEndsOfAPeriodAsUtc()
        {
            var parsed = GraphOpening.Parse("2026-07-31T11:50:00Z", "2026-07-31T12:30:00", null);

            Assert.Multiple(() =>
            {
                // The second end carries no zone, and is read as UTC rather than as the local time
                // of whichever machine the link was opened against.
                Assert.That(
                    parsed,
                    Is.EqualTo(new GraphOpening.Period(Start.AddMinutes(-10), Start.AddMinutes(30))));
                Assert.That(((GraphOpening.Period)parsed!).StartUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
                Assert.That(((GraphOpening.Period)parsed).EndUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            });
        }

        /// <summary>
        /// An address carrying both says two things at once, and the one with instants in it is the
        /// more particular of them.
        /// </summary>
        [Test]
        public void Parse_PrefersThePeriodWhereAnAddressCarriesBoth()
        {
            var parsed = GraphOpening.Parse("2026-07-31T11:50:00Z", "2026-07-31T12:30:00Z", "5400");

            Assert.That(parsed, Is.InstanceOf<GraphOpening.Period>());
        }

        /// <summary>
        /// A hand-typed or truncated address leaves the reader looking at a live chart rather than
        /// at an error page, so anything that is neither two readable instants in order nor a width
        /// the chart will draw is no opening at all.
        /// </summary>
        [TestCase(null, null, null, TestName = "Parse_IgnoresAnOpeningItCannotUse(nothing at all)")]
        [TestCase("2026-07-31T11:50:00Z", null, null, TestName = "Parse_IgnoresAnOpeningItCannotUse(one end only)")]
        [TestCase("yesterday", "2026-07-31T12:30:00Z", null, TestName = "Parse_IgnoresAnOpeningItCannotUse(not a time)")]
        [TestCase("2026-07-31T12:30:00Z", "2026-07-31T11:50:00Z", null, TestName = "Parse_IgnoresAnOpeningItCannotUse(backwards)")]
        [TestCase("2026-07-31T12:30:00Z", "2026-07-31T12:30:00Z", null, TestName = "Parse_IgnoresAnOpeningItCannotUse(no width)")]
        [TestCase(null, null, "an hour", TestName = "Parse_IgnoresAnOpeningItCannotUse(not a width)")]
        [TestCase(null, null, "0", TestName = "Parse_IgnoresAnOpeningItCannotUse(no time at all)")]
        [TestCase(null, null, "-5400", TestName = "Parse_IgnoresAnOpeningItCannotUse(backwards width)")]
        [TestCase(null, null, "999999999999999999999", TestName = "Parse_IgnoresAnOpeningItCannotUse(more time than there is)")]
        [TestCase(null, null, "31536001", TestName = "Parse_IgnoresAnOpeningItCannotUse(wider than the cap)")]
        public void Parse_IgnoresAnOpeningItCannotUse(string? start, string? end, string? window)
        {
            Assert.That(GraphOpening.Parse(start, end, window), Is.Null);
        }

        /// <summary>
        /// The other end of the same guard: a width this narrow is read rather than refused, and
        /// comes back as the narrowest the chart draws.
        /// </summary>
        [Test]
        public void Parse_WidensAWindowTooNarrowToRead()
        {
            Assert.That(GraphOpening.Parse(null, null, "2"), Is.EqualTo(new GraphOpening.Window(TimeSpan.FromMinutes(1))));
        }
    }
}
