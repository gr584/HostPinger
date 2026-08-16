using HostPinger.Core.Data;

namespace HostPinger.Test
{
    /// <summary>
    /// The order the Hosts page puts its rows in. Built from summaries directly rather than through
    /// the database: what <see cref="HostSummary.LoadAsync"/> reads is settled in
    /// <see cref="HostSummaryTests"/>, and everything here is about what happens to those rows
    /// afterwards.
    /// </summary>
    public class HostSortTests
    {
        private static readonly DateTime Start = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public void Default_OrdersByNameAToZ()
        {
            var sorted = HostSort.Default.Apply([Row("charlie"), Row("alpha"), Row("bravo")]);

            Assert.Multiple(() =>
            {
                Assert.That(Names(sorted), Is.EqualTo(new[] { "alpha", "bravo", "charlie" }));
                Assert.That(HostSort.Default.Column, Is.EqualTo(HostSortColumn.Name));
                Assert.That(HostSort.Default.IsDescending, Is.False);
            });
        }

        /// <summary>
        /// Case decides nothing about where a name belongs. SQLite's own ordering is by code point,
        /// which files every capitalised name ahead of every lower-case one — "Router" before
        /// "gateway" — and a list of host names is not read that way.
        /// </summary>
        [Test]
        public void Apply_OrdersNamesWithoutRegardToCase()
        {
            var sorted = HostSort.Default.Apply([Row("router"), Row("Gateway"), Row("NAS")]);

            Assert.That(Names(sorted), Is.EqualTo(new[] { "Gateway", "NAS", "router" }));
        }

        [Test]
        public void Apply_OrdersByAddress()
        {
            var rows = new[]
            {
                Row("third", address: "192.168.1.30"),
                Row("first", address: "10.0.0.1"),
                Row("second", address: "172.16.0.5"),
            };

            Assert.That(
                Names(HostSort.For(HostSortColumn.Address).Apply(rows)),
                Is.EqualTo(new[] { "first", "second", "third" }));
        }

        /// <summary>
        /// The point of sorting by status: one click brings whatever is wrong to the top. The order
        /// is <see cref="HostStatus"/>'s own, which is already the order a host that stops answering
        /// moves through, read from its far end.
        /// </summary>
        [Test]
        public void Apply_LeadsWithTheWorstStatus()
        {
            var rows = new[]
            {
                Row("up", HostStatus.Up),
                Row("paused", HostStatus.Paused),
                Row("down", HostStatus.Down),
                Row("waiting", HostStatus.Waiting),
                Row("retrying", HostStatus.Retrying),
            };

            Assert.That(
                Names(HostSort.For(HostSortColumn.Status).Apply(rows)),
                Is.EqualTo(new[] { "down", "retrying", "up", "waiting", "paused" }));
        }

        /// <summary>
        /// A host that answered nothing is the slow end of the Last ping column rather than a gap in
        /// it: "no reply" is a worse answer than any round trip, not a missing one. A host no round
        /// has covered yet is the missing one, and stays out of the way at the end.
        /// </summary>
        [Test]
        public void Apply_RanksAnUnansweredPingBehindEveryRoundTrip()
        {
            var rows = new[]
            {
                Row("quick", roundtripMs: 4),
                Row("never", isPinged: false),
                Row("slow", roundtripMs: 900),
                Row("silent", roundtripMs: null),
            };

            Assert.That(
                Names(HostSort.For(HostSortColumn.LastPing).Apply(rows)),
                Is.EqualTo(new[] { "silent", "slow", "quick", "never" }));
        }

        [Test]
        public void Apply_OrdersTheFastestPingFirstWhenTurnedRound()
        {
            var rows = new[]
            {
                Row("quick", roundtripMs: 4),
                Row("never", isPinged: false),
                Row("slow", roundtripMs: 900),
                Row("silent", roundtripMs: null),
            };

            var sorted = HostSort.For(HostSortColumn.LastPing).ClickedOn(HostSortColumn.LastPing).Apply(rows);

            Assert.That(Names(sorted), Is.EqualTo(new[] { "quick", "slow", "silent", "never" }));
        }

        [Test]
        public void Apply_LeadsWithTheMostRecentDowntime()
        {
            var rows = new[]
            {
                Row("old", downtimeStartedUtc: Start.AddDays(-7)),
                Row("clean"),
                Row("recent", downtimeStartedUtc: Start.AddMinutes(-5)),
            };

            Assert.That(
                Names(HostSort.For(HostSortColumn.LastDowntime).Apply(rows)),
                Is.EqualTo(new[] { "recent", "old", "clean" }));
        }

        /// <summary>
        /// A column of dashes answers neither "which is worst" nor "which is best", so a host with
        /// nothing in the column sorted on is never what the reader is brought to the top to see.
        /// Turning the column round is how the oldest outage is found, not how the hosts that have
        /// never had one are.
        /// </summary>
        [Test]
        public void Apply_KeepsHostsWithNothingInTheColumnAtTheEndEitherWay()
        {
            var rows = new[]
            {
                Row("clean"),
                Row("old", downtimeStartedUtc: Start.AddDays(-7)),
                Row("recent", downtimeStartedUtc: Start.AddMinutes(-5)),
            };

            var descending = HostSort.For(HostSortColumn.LastDowntime);
            var ascending = descending.ClickedOn(HostSortColumn.LastDowntime);

            Assert.Multiple(() =>
            {
                Assert.That(Names(descending.Apply(rows)).Last(), Is.EqualTo("clean"));
                Assert.That(Names(ascending.Apply(rows)), Is.EqualTo(new[] { "old", "recent", "clean" }));
            });
        }

        /// <summary>
        /// Hosts that tie keep the order they had before anything was sorted, rather than swapping
        /// places from one five-second refresh to the next.
        /// </summary>
        [TestCase(false, TestName = "Apply_BreaksTiesByNameAscending")]
        [TestCase(true, TestName = "Apply_BreaksTiesByNameAscendingWhenTurnedRound")]
        public void Apply_BreaksTiesByName(bool isDescending)
        {
            var rows = new[]
            {
                Row("charlie", HostStatus.Down),
                Row("alpha", HostStatus.Down),
                Row("bravo", HostStatus.Down),
            };

            var sorted = new HostSort(HostSortColumn.Status, isDescending).Apply(rows);

            Assert.That(Names(sorted), Is.EqualTo(new[] { "alpha", "bravo", "charlie" }));
        }

        [Test]
        public void Apply_LeavesTheRowsItWasGivenAlone()
        {
            var rows = new List<HostSummary> { Row("charlie"), Row("alpha") };

            HostSort.For(HostSortColumn.Name).Apply(rows);

            Assert.That(Names(rows), Is.EqualTo(new[] { "charlie", "alpha" }));
        }

        /// <summary>
        /// The text columns read the way a list of names is expected to; the three that say how a
        /// host is doing lead with the worst of it, which is the reason to sort by them at all.
        /// </summary>
        [TestCase(HostSortColumn.Name, false)]
        [TestCase(HostSortColumn.Address, false)]
        [TestCase(HostSortColumn.Status, true)]
        [TestCase(HostSortColumn.LastPing, true)]
        [TestCase(HostSortColumn.LastDowntime, true)]
        public void For_StartsEachColumnAtTheEndWorthReadingFirst(HostSortColumn column, bool expected)
        {
            Assert.That(HostSort.For(column).IsDescending, Is.EqualTo(expected));
        }

        [Test]
        public void ClickedOn_TurnsTheSortedColumnRound()
        {
            var sorted = HostSort.Default.ClickedOn(HostSortColumn.Name);

            Assert.Multiple(() =>
            {
                Assert.That(sorted.Column, Is.EqualTo(HostSortColumn.Name));
                Assert.That(sorted.IsDescending, Is.True);
                Assert.That(sorted.ClickedOn(HostSortColumn.Name), Is.EqualTo(HostSort.Default));
            });
        }

        /// <summary>
        /// Another column arrives at its own leading end rather than inheriting the direction of the
        /// one before it, so that clicking Status always brings what is down to the top.
        /// </summary>
        [Test]
        public void ClickedOn_TakesAnotherColumnToItsOwnLeadingEnd()
        {
            var turnedRound = HostSort.Default.ClickedOn(HostSortColumn.Name);

            Assert.That(turnedRound.ClickedOn(HostSortColumn.Status), Is.EqualTo(HostSort.For(HostSortColumn.Status)));
        }

        [Test]
        public void Parse_ReadsBackWhatTheQueryPropertiesWrite()
        {
            foreach (var column in Enum.GetValues<HostSortColumn>())
            {
                foreach (var sort in new[] { HostSort.For(column), HostSort.For(column).ClickedOn(column) })
                {
                    Assert.That(HostSort.Parse(sort.QueryColumn, sort.QueryDirection), Is.EqualTo(sort));
                }
            }
        }

        /// <summary>
        /// The page's own link is the bare one: a reader who has sorted their way back to where they
        /// started is not left carrying a query string that says so.
        /// </summary>
        [Test]
        public void QueryColumn_LeavesTheDefaultOrderOutOfTheAddress()
        {
            Assert.Multiple(() =>
            {
                Assert.That(HostSort.Default.QueryColumn, Is.Null);
                Assert.That(HostSort.Default.QueryDirection, Is.Null);
                Assert.That(HostSort.For(HostSortColumn.LastPing).QueryColumn, Is.EqualTo("last-ping"));
                Assert.That(HostSort.For(HostSortColumn.LastPing).QueryDirection, Is.EqualTo("desc"));
            });
        }

        [Test]
        public void Parse_ReadsAColumnWhateverCaseItIsWrittenIn()
        {
            Assert.That(
                HostSort.Parse("Last-Downtime", "ASC"),
                Is.EqualTo(new HostSort(HostSortColumn.LastDowntime, IsDescending: false)));
        }

        /// <summary>
        /// A hand-typed or stale query string leaves the reader looking at a table rather than at an
        /// error page. A column named without a readable direction is ordered the way clicking that
        /// heading would order it, which is what a shortened link most likely meant.
        /// </summary>
        [TestCase(null, null)]
        [TestCase("", "")]
        [TestCase("uptime", "desc")]
        [TestCase(null, "desc")]
        public void Parse_FallsBackToTheDefaultForAnythingItCannotRead(string? column, string? direction)
        {
            Assert.That(HostSort.Parse(column, direction), Is.EqualTo(HostSort.Default));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("sideways")]
        public void Parse_ReadsAColumnWithoutADirectionTheWayClickingItWould(string? direction)
        {
            Assert.That(HostSort.Parse("status", direction), Is.EqualTo(HostSort.For(HostSortColumn.Status)));
        }

        private static IEnumerable<string> Names(IEnumerable<HostSummary> rows) => rows.Select(row => row.Host.Name);

        /// <summary>
        /// One host as the page would have it, defaulting to a host that is answering and has never
        /// been down — so that each test states only the column it is about.
        /// </summary>
        /// <param name="name">The host's name.</param>
        /// <param name="status">Where the host stands.</param>
        /// <param name="address">Its address; derived from the name when not given.</param>
        /// <param name="roundtripMs">The round trip of its last ping, or null for no reply.</param>
        /// <param name="isPinged">False for a host no round has covered yet, which has no last ping.</param>
        /// <param name="downtimeStartedUtc">When its last downtime began, or null for a host that has had none.</param>
        private static HostSummary Row(
            string name,
            HostStatus status = HostStatus.Up,
            string? address = null,
            int? roundtripMs = 10,
            bool isPinged = true,
            DateTime? downtimeStartedUtc = null) =>
            new(
                new MonitoredHost { Name = name, Address = address ?? $"{name}.example" },
                isPinged ? new LastPing(Start, roundtripMs) : null,
                downtimeStartedUtc is { } started ? new Downtime(started, Start) : null,
                MissedPings: 0,
                status);
    }
}
