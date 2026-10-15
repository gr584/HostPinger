using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The Hosts page keeping up with the database, both ways a change reaches it: one the page made
    /// itself, and one that lands while it is open. What the summary does in either case is settled
    /// in HostSummaryCacheTests; what only a browser can settle is that the page is listening — that
    /// the switch reaches it, and that what the application reads next is drawn without a reload.
    /// </summary>
    /// <remarks>
    /// Not parallel with anything, because changing a host takes an unlocked browser, and the
    /// password is checked against one tally for every fixture: LockOverlayTests runs it out on
    /// purpose, and an unlock landing while it waits would be turned away.
    /// </remarks>
    [NonParallelizable]
    public class HostsPageChangeTests : BrowserTest
    {
        /// <summary>
        /// Hosts of this fixture's own, so that nothing here changes what another test looks at, and
        /// each test's host is in the database only once the test has put it there.
        /// </summary>
        private const string ArrivingName = "change-arriving";

        private const string PausedName = "change-paused";

        /// <summary>
        /// A host written while the page is open, the way a round's attempts are: from outside the
        /// page, with nothing to tell it. It turns up in the table on its own.
        /// </summary>
        [Test]
        public async Task Table_TakesInAHostAddedWhileItIsOpen()
        {
            await GoAsync();
            await ListeningAsync();
            await Assertions.Expect(Row(ArrivingName)).ToHaveCountAsync(0);

            await SeedAsync(ArrivingName);

            await Assertions.Expect(Status(ArrivingName)).ToHaveTextAsync("Up");
        }

        [Test]
        public async Task Switch_PausesTheHostAndResumesIt()
        {
            await SeedAsync(PausedName);
            await GoAsync();
            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();

            // The unlock is a navigation, so the page it came back to needs a circuit of its own.
            await GoAsync();
            await ListeningAsync();
            await Assertions.Expect(Status(PausedName)).ToHaveTextAsync("Up");

            await Switch(PausedName).UncheckAsync();
            await Assertions.Expect(Status(PausedName)).ToHaveTextAsync("Paused");

            await Switch(PausedName).CheckAsync();
            await Assertions.Expect(Status(PausedName)).ToHaveTextAsync("Up");
        }

        private ILocator Row(string name) => Page.Locator(".host-row", new PageLocatorOptions { HasText = name });

        private ILocator Status(string name) => Row(name).Locator(".badge");

        private ILocator Switch(string name) => Row(name).GetByRole(AriaRole.Switch);

        /// <summary>
        /// Waits until the page is listening, which a switch cannot be used to find out: unlike the
        /// buttons ClickUntilAsync is for, a click the page did not hear still moves a switch in the
        /// browser, and a second click to make sure moves it back. A heading can be clicked until it
        /// takes, and once it has sorted the table, the page is attached to its circuit.
        /// </summary>
        private Task ListeningAsync() => ClickUntilAsync(Button("Name"), Page.Locator("th[aria-sort='descending']"));

        /// <summary>
        /// Writes to the running application's own database, the way HostsPageTests does: a host
        /// that answered its one ping, so that it reads as up while it is being pinged. The address
        /// does not resolve, so the rounds running alongside this record their failure to look it up
        /// and never add an attempt of their own to change what it reads as.
        /// </summary>
        private static async Task SeedAsync(string name)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

            await using var db = new HostPingerDbContext(options);
            var host = new MonitoredHost { Name = name, Address = $"{name}.example", CreatedUtc = DateTime.UtcNow };
            db.Hosts.Add(host);
            await UiTestRun.SaveAsync(db);
            db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = DateTime.UtcNow, RoundtripMs = 20 });
            await UiTestRun.SaveAsync(db);
        }
    }
}
