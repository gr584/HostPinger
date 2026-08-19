using System.Text.RegularExpressions;
using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The Last downtime cell of the hosts table, which opens the graph on the outage it reports
    /// rather than on the live window the rest of the row opens. What only a browser can settle is
    /// that a click on that cell is heard by the cell rather than by the row it sits in, that what
    /// it works out survives the trip through the address, and that the graph it lands on reads it
    /// back — holding still on an outage that is over, and still following the clock on one that is
    /// not.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    public class DowntimeGraphTests : BrowserTest
    {
        /// <summary>
        /// A host per outage, of this fixture's own, so that nothing another test seeds can be what
        /// is clicked here and neither of these can be the other.
        /// </summary>
        private const string RecoveredName = "downtime-recovered";

        private const string StillDownName = "downtime-still-down";

        /// <summary>
        /// How the two ends of a period are written into the address, spelled out here rather than
        /// taken from <see cref="Core.Charting.GraphOpening"/> so that a change to either has to be
        /// a change to a link people may have kept.
        /// </summary>
        private const string QueryFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        /// <summary>
        /// An outage that is over is a stretch of the past: the chart is pinned to it, twice as
        /// long as the outage with the outage in the middle.
        /// </summary>
        [Test]
        public async Task LastDowntime_HoldsStillOnAnOutageThatIsOver()
        {
            var anchor = Anchor();
            var hostId = await SeedAsync(RecoveredName, anchor, lastMissedMinutesAgo: 30, answeredAgainMinutesAgo: 25);
            await GoAsync();

            await ClickUntilAsync(DowntimeCell(RecoveredName), Page.Locator(".range-state"));

            // The outage ran from 60 minutes back to 25, so it is 35 minutes long: the range is
            // twice that, from 17 minutes 30 seconds before it began to the same after it ended.
            var from = anchor.AddMinutes(-77.5).ToString(QueryFormat);
            var to = anchor.AddMinutes(-7.5).ToString(QueryFormat);
            Assert.Multiple(async () =>
            {
                await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/graph/{hostId}?from={from}&to={to}");

                // Paused, so the outage stays on screen instead of being dragged back to the
                // present five seconds after it was opened.
                await Assertions.Expect(Page.Locator(".range-bar .badge")).ToHaveTextAsync("Paused");
                await Assertions.Expect(Page.Locator(".range-state")).ToHaveTextAsync("Custom range");
                await Assertions.Expect(Page.Locator(".range-duration")).ToHaveTextAsync("1h 10m");
            });
        }

        /// <summary>
        /// An outage that is still running has no end to be centred on, and freezing the chart on
        /// one would leave the reader watching a picture of something that is still happening. The
        /// chart stays live, over a window one and a half times as long as the outage so far — so
        /// the outage fills the last two thirds of it as it opens, and goes on filling more of it.
        /// </summary>
        [Test]
        public async Task LastDowntime_GoesOnFollowingTheClockOnAnOutageThatIsNot()
        {
            var anchor = Anchor();
            var hostId = await SeedAsync(StillDownName, anchor, lastMissedMinutesAgo: 5, answeredAgainMinutesAgo: null);
            await GoAsync();

            await ClickUntilAsync(DowntimeCell(StillDownName), Page.Locator(".range-state"));

            // The outage began 60 minutes back and is still running, so the window is 90 minutes —
            // give or take the seconds the page took to be clicked, which are part of the outage.
            Assert.Multiple(async () =>
            {
                await Assertions.Expect(Page).ToHaveURLAsync(new Regex($@"/graph/{hostId}\?window=54\d\d$"));
                await Assertions.Expect(Page.Locator(".range-bar .badge")).ToHaveTextAsync("Live");
                await Assertions.Expect(Page.Locator(".range-state")).ToHaveTextAsync(new Regex(@"^Last 1h 3\dm$"));
            });
        }

        private ILocator DowntimeCell(string name) =>
            Page.Locator(".host-row", new PageLocatorOptions { HasText = name }).Locator("td.last-downtime");

        /// <summary>
        /// Whole seconds, because that is as fine as the address carries and the expectations above
        /// are written out to match it exactly.
        /// </summary>
        private static DateTime Anchor() =>
            new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        /// <summary>
        /// Writes to the running application's own database, the way HostsPageTests does: a host
        /// that answered an hour ago and then stopped, with a ping every five minutes since. The
        /// answered ping is where a downtime is measured from and the ping that answers again is
        /// where it ends, so those two are what the graph's range is built out of. Six missed pings
        /// at least, which is more than any retry count the application ships with, so the run
        /// counts as an outage rather than as a host that is still retrying. The address does not
        /// resolve, so the rounds running alongside this — were the interval short enough for one
        /// to run at all — could only record a failure to look it up, never an attempt of its own
        /// to argue with these.
        /// </summary>
        /// <param name="name">The host's name, which is also what its address is built from.</param>
        /// <param name="anchorUtc">The instant the timeline below is measured back from.</param>
        /// <param name="lastMissedMinutesAgo">How recently it last missed a ping.</param>
        /// <param name="answeredAgainMinutesAgo">
        /// When it answered again, or null for a host that has not: the whole of the difference
        /// between an outage that is over and one that is still running.
        /// </param>
        private static async Task<int> SeedAsync(
            string name,
            DateTime anchorUtc,
            int lastMissedMinutesAgo,
            int? answeredAgainMinutesAgo)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

            var address = $"{name}.example";
            await using var db = new HostPingerDbContext(options);
            var host = await db.Hosts.FirstOrDefaultAsync(h => h.Address == address);
            if (host is not null)
            {
                return host.Id;
            }

            host = new MonitoredHost { Name = name, Address = address, CreatedUtc = anchorUtc.AddDays(-1) };
            db.Hosts.Add(host);
            await UiTestRun.SaveAsync(db);

            db.PingAttempts.Add(Attempt(host.Id, anchorUtc.AddMinutes(-60), 10));
            for (var minutes = 55; minutes >= lastMissedMinutesAgo; minutes -= 5)
            {
                db.PingAttempts.Add(Attempt(host.Id, anchorUtc.AddMinutes(-minutes), null));
            }

            if (answeredAgainMinutesAgo is int answeredAgain)
            {
                db.PingAttempts.Add(Attempt(host.Id, anchorUtc.AddMinutes(-answeredAgain), 12));
            }

            await UiTestRun.SaveAsync(db);
            return host.Id;
        }

        private static PingAttempt Attempt(int hostId, DateTime timestampUtc, int? roundtripMs) =>
            new() { HostId = hostId, TimestampUtc = timestampUtc, RoundtripMs = roundtripMs };
    }
}
