using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// What the graph measures a hole in the line against: the interval the pinger is actually
    /// running at, which is the one the Configuration page stores, rather than the default in
    /// appsettings.json that the stored one overrides.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    public class GraphIntervalTests : BrowserTest
    {
        /// <summary>A host of this fixture's own, so that nothing another test seeds is plotted here.</summary>
        private const string HostName = "graph-interval";

        /// <summary>
        /// The line breaks where pings are more than ten of the pinger's intervals apart. This run
        /// stores an interval of a day (see <see cref="WebApp"/>), which a 400-second hole comes
        /// nowhere near; read from appsettings.json instead, the interval was the 30-second default,
        /// and the same hole broke the line in two.
        /// </summary>
        [Test]
        public async Task Graph_MeasuresHolesAgainstTheStoredInterval()
        {
            var hostId = await SeedAsync(DateTime.UtcNow);

            await GoAsync($"/graph/{hostId}");

            await Assertions.Expect(Page.Locator("svg polyline")).ToHaveCountAsync(1);
        }

        /// <summary>
        /// Answered pings a minute apart, with a 400-second hole in the middle of them, all inside the
        /// hour the graph opens on.
        /// </summary>
        private static async Task<int> SeedAsync(DateTime nowUtc)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

            var address = $"{HostName}.example";
            await using var db = new HostPingerDbContext(options);
            var host = await db.Hosts.FirstOrDefaultAsync(h => h.Address == address);
            if (host is not null)
            {
                return host.Id;
            }

            host = new MonitoredHost { Name = HostName, Address = address, CreatedUtc = nowUtc.AddDays(-1) };
            db.Hosts.Add(host);
            await UiTestRun.SaveAsync(db);

            int[] secondsAgo = [900, 840, 780, 380, 320, 260];
            foreach (var ago in secondsAgo)
            {
                db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = nowUtc.AddSeconds(-ago), RoundtripMs = 10 });
            }

            await UiTestRun.SaveAsync(db);
            return host.Id;
        }
    }
}
