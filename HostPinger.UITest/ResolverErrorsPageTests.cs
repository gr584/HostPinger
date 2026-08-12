using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The resolver errors page against a real instance, from rows in the database to what a reader
    /// sees. What only a browser can settle is that the page is reachable from the navigation at
    /// all, and that every failure of one address arrives as the single line the page is built
    /// around rather than one line per round.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    public class ResolverErrorsPageTests : BrowserTest
    {
        /// <summary>Its own address, so that nothing another test seeds can land in this one's row.</summary>
        private const string Address = "resolver-errors-test.example";

        [Test]
        public async Task Nav_OffersTheResolverErrorsBetweenTheGraphAndTheConfiguration()
        {
            await GoAsync();

            await Assertions.Expect(Page.Locator(".nav-scrollable .nav-link")).ToHaveTextAsync(
                ["Hosts", "Graph", "Resolver errors", "Configuration", "About"]);
        }

        /// <summary>
        /// Every failure of an address folds into one row: when it last failed, how it failed, and
        /// how often it has lately. The counts are what the row is for — a name that is failing
        /// every round reads differently from one that failed once last week — so the seeded
        /// failures are spread to give each of the three windows a different number.
        /// </summary>
        [Test]
        public async Task Page_FoldsEveryFailureOfAnAddressIntoOneRowWithItsRecentCounts()
        {
            var now = DateTime.UtcNow;
            await SeedAsync(
                new MonitoredHost { Name = "Seeded host", Address = Address, CreatedUtc = now },
                new ResolverError { Address = Address, TimestampUtc = now.AddDays(-20), Reason = ResolverFailure.LookupFailed },
                new ResolverError { Address = Address, TimestampUtc = now.AddDays(-3), Reason = ResolverFailure.LookupFailed },
                new ResolverError { Address = Address, TimestampUtc = now.AddHours(-2), Reason = ResolverFailure.NoAddresses },
                new ResolverError { Address = Address, TimestampUtc = now.AddMinutes(-2), Reason = ResolverFailure.TimedOut });

            await GoAsync("/resolver-errors");

            var cells = Page.Locator("tr", new PageLocatorOptions { HasText = Address }).Locator("td");
            await Assertions.Expect(cells).ToHaveCountAsync(7);
            Assert.Multiple(async () =>
            {
                await Assertions.Expect(cells.Nth(1)).ToHaveTextAsync("Seeded host");

                // The reason of the most recent failure, not of the first one recorded.
                await Assertions.Expect(cells.Nth(2)).ToHaveTextAsync("Timed out");
                await Assertions.Expect(cells.Nth(3)).ToContainTextAsync("2m ago");
                await Assertions.Expect(cells.Nth(4)).ToHaveTextAsync("2");
                await Assertions.Expect(cells.Nth(5)).ToHaveTextAsync("3");
                await Assertions.Expect(cells.Nth(6)).ToHaveTextAsync("4");
            });
        }

        /// <summary>
        /// Writes to the running application's own database, which is how a page that only reads
        /// gets something to show. Nothing is removed afterwards: the run's database is temporary,
        /// and the address above belongs to this fixture alone.
        /// </summary>
        private static async Task SeedAsync(MonitoredHost host, params ResolverError[] errors)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

            await using var db = new HostPingerDbContext(options);
            if (!await db.Hosts.AnyAsync(h => h.Address == host.Address))
            {
                db.Hosts.Add(host);
            }

            db.ResolverErrors.AddRange(errors);
            await db.SaveChangesAsync();
        }
    }
}
