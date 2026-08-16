using System.Text.RegularExpressions;
using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// Sorting the hosts table, driven the way a person drives it. What order
    /// <see cref="HostSort"/> puts rows in is settled in HostSortTests; what only a browser can
    /// settle is that a heading is something a click lands on at all, that the order it produces
    /// rides in the address rather than in the circuit, and that the address it leaves behind
    /// brings the same table back.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    public class HostsPageTests : BrowserTest
    {
        /// <summary>
        /// A prefix of this fixture's own, so that the rows another test seeds — and any the
        /// application itself has — can be told apart from these three and left out of the order
        /// being asserted on.
        /// </summary>
        private const string Prefix = "sort-";

        /// <summary>
        /// Three hosts whose names and round trips disagree, so that an assertion about one column
        /// cannot pass on the ordering of another: by name they read alpha, bravo, charlie, and by
        /// round trip bravo, charlie, alpha.
        /// </summary>
        private static readonly (string Name, int RoundtripMs)[] Seeded =
        [
            ($"{Prefix}alpha", 5),
            ($"{Prefix}bravo", 500),
            ($"{Prefix}charlie", 50),
        ];

        /// <summary>This fixture's rows, in the order the page has them.</summary>
        private ILocator Names =>
            Page.Locator(".host-row", new PageLocatorOptions { HasText = Prefix }).Locator("td:first-child");

        [Test]
        public async Task Headings_SortTheTableAndCarryTheOrderInTheAddress()
        {
            await SeedAsync();
            await GoAsync();
            await Assertions.Expect(Names).ToHaveTextAsync([$"{Prefix}alpha", $"{Prefix}bravo", $"{Prefix}charlie"]);

            await ClickUntilAsync(Button("Name"), Page.Locator("th[aria-sort='descending']"));

            Assert.Multiple(async () =>
            {
                await Assertions.Expect(Names).ToHaveTextAsync(
                    [$"{Prefix}charlie", $"{Prefix}bravo", $"{Prefix}alpha"]);
                await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"\?sort=name&dir=desc$"));
            });
        }

        /// <summary>
        /// The address a sorted table leaves behind is the whole of what it takes to bring that
        /// table back — which is what lets a row be opened and returned from with the sort intact,
        /// and what makes the table worth sending to someone.
        /// </summary>
        [Test]
        public async Task Address_BringsBackTheTableItWasTakenFrom()
        {
            await SeedAsync();

            await GoAsync("/?sort=last-ping&dir=desc");

            // The slowest first, and by round trip rather than by name.
            await Assertions.Expect(Names).ToHaveTextAsync(
                [$"{Prefix}bravo", $"{Prefix}charlie", $"{Prefix}alpha"]);
        }

        /// <summary>
        /// Sorting back to where the page starts takes the query string off again rather than
        /// spelling the default out, so the reader is not left carrying one that says nothing.
        /// </summary>
        [Test]
        public async Task Headings_LeaveThePlainAddressBehindWhenTheOrderIsBackToTheDefault()
        {
            await SeedAsync();
            await GoAsync();

            await ClickUntilAsync(Button("Name"), Page.Locator("th[aria-sort='descending']"));
            await ClickUntilAsync(Button("Name"), Page.Locator("th[aria-sort='ascending']"));

            await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/");
        }

        /// <summary>
        /// Writes to the running application's own database, the way ResolverErrorsPageTests does.
        /// The attempts are what give the hosts a round trip to be sorted on; the addresses do not
        /// resolve, so the rounds running alongside this record their failure to look them up and
        /// never add an attempt of their own to argue with these.
        /// </summary>
        private static async Task SeedAsync()
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

            await using var db = new HostPingerDbContext(options);
            foreach (var (name, roundtripMs) in Seeded)
            {
                var address = $"{name}.example";
                if (await db.Hosts.AnyAsync(h => h.Address == address))
                {
                    continue;
                }

                var host = new MonitoredHost { Name = name, Address = address, CreatedUtc = DateTime.UtcNow };
                db.Hosts.Add(host);
                await db.SaveChangesAsync();
                db.PingAttempts.Add(new PingAttempt
                {
                    HostId = host.Id,
                    TimestampUtc = DateTime.UtcNow,
                    RoundtripMs = roundtripMs,
                });
                await db.SaveChangesAsync();
            }
        }
    }
}
