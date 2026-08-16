using System.Text.RegularExpressions;
using HostPinger.Core.Charting;
using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The graph page's host picker. Both halves of it need a real browser: the panel is only in the
    /// markup once it has been opened, and the swatch beside a name is a colour the page works out
    /// from where that host sits in a list the reader never sees.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    public class GraphPickerTests : BrowserTest
    {
        /// <summary>
        /// A prefix of this fixture's own, so that the hosts another test seeds can be left out of
        /// the order being asserted on.
        /// </summary>
        private const string Prefix = "picker-";

        /// <summary>
        /// Seeded in an order that is not their alphabetical one, so that a picker merely showing
        /// them in the order they were added cannot pass for a sorted one.
        /// </summary>
        private static readonly string[] AddedInOrder =
        [
            $"{Prefix}zulu",
            $"{Prefix}alpha",
            $"{Prefix}mike",
        ];

        /// <summary>This fixture's rows of the picker, in the order the panel has them.</summary>
        private ILocator Rows =>
            Page.Locator(".picker-list .form-check", new PageLocatorOptions { HasText = Prefix })
                .Locator(".form-check-label");

        [Test]
        public async Task Picker_ListsHostsByNameWhateverOrderTheyWereAddedIn()
        {
            await SeedAsync();
            await GoAsync("/graph");

            await OpenPickerAsync();

            await Assertions.Expect(Rows).ToHaveTextAsync(
            [
                new Regex($"^{Prefix}alpha"),
                new Regex($"^{Prefix}mike"),
                new Regex($"^{Prefix}zulu"),
            ]);
        }

        /// <summary>
        /// The swatch says which line on the chart is this host's, so it has to belong to the host
        /// rather than to the row it is sitting in. Searching is the cheapest way to move a host up
        /// the list without touching the hosts themselves: filtering to one leaves it at the top,
        /// where a colour taken from the row's position would come out as the first colour of the
        /// palette. This is what the alphabetical order above has to leave alone — a picker that
        /// repainted the chart it is used to read would be worse than an unsorted one.
        /// </summary>
        [Test]
        public async Task Picker_KeepsAHostsColourWhenTheListAroundItChanges()
        {
            await SeedAsync();
            await GoAsync("/graph");
            await OpenPickerAsync();
            var (name, colour) = await AHostNotWearingTheFirstColourAsync();

            await Page.Locator(".picker-panel input[type=search]").FillAsync(name);

            await Assertions.Expect(Rows).ToHaveCountAsync(1);
            Assert.That(await Swatch(name).GetAttributeAsync("style"), Is.EqualTo(colour));
        }

        /// <summary>
        /// One of this fixture's hosts together with the colour it is listed in, chosen so that the
        /// assertion above can fail: a colour taken from the row's position would be the first of
        /// the palette once the search has left that host alone at the top, so a host that already
        /// wears the first colour would pass whatever the page did. Which of the three that is
        /// depends on what else the run has put in the database ahead of them, so it is found here
        /// rather than assumed — and at most one of the three can be wearing it.
        /// </summary>
        private async Task<(string Name, string Colour)> AHostNotWearingTheFirstColourAsync()
        {
            var listed = new List<(string Name, string Colour)>();
            foreach (var name in AddedInOrder)
            {
                listed.Add((name, await Swatch(name).GetAttributeAsync("style") ?? string.Empty));
            }

            var candidates = listed
                .Where(host => !host.Colour.Contains(ChartPalette.GetColor(0), StringComparison.Ordinal))
                .ToList();

            Assert.That(candidates, Is.Not.Empty, $"None of {string.Join(", ", AddedInOrder)} was listed at all.");
            return candidates[0];
        }

        private ILocator Swatch(string name) =>
            Page.Locator(".picker-list .form-check", new PageLocatorOptions { HasText = name }).Locator(".swatch");

        /// <summary>
        /// Opens the panel, which is in the markup only while it is open. The button is clicked
        /// until it opens for the reason given on <see cref="BrowserTest.ClickUntilAsync"/>.
        /// </summary>
        private async Task OpenPickerAsync() =>
            await ClickUntilAsync(Page.Locator(".host-picker .dropdown-toggle"), Page.Locator(".picker-panel"));

        /// <summary>
        /// Writes to the running application's own database, the way ResolverErrorsPageTests does.
        /// One at a time and in this order, because the ids they are given are what the colours
        /// follow and this fixture is about those two orders differing.
        /// </summary>
        private static async Task SeedAsync()
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

            await using var db = new HostPingerDbContext(options);
            foreach (var name in AddedInOrder)
            {
                var address = $"{name}.example";
                if (await db.Hosts.AnyAsync(h => h.Address == address))
                {
                    continue;
                }

                db.Hosts.Add(new MonitoredHost { Name = name, Address = address, CreatedUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        }
    }
}
