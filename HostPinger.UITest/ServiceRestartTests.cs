using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// A restart of the service locks every browser that was unlocked, whatever its cookie says.
    /// Each test starts an instance of its own, since restarting the run's would pull it out from
    /// under every other test.
    /// </summary>
    public class ServiceRestartTests : BrowserTest
    {
        /// <summary>The name <see cref="HostPinger.Security.UnlockCookie"/> gives the cookie.</summary>
        private const string UnlockCookieName = "hostpinger-unlock";

        /// <summary>
        /// The browser keeps its cookie across the restart, and the restarted service can still
        /// read it — the data protection keys are on disk beside the database — so it is the
        /// service that turns the unlock away, not the browser that lost it. Unlocking again
        /// afterwards works, under the instance now running.
        /// </summary>
        [Test]
        public async Task ARestartLocksABrowserThatWasUnlocked()
        {
            await using var app = await WebApp.StartAsync(new WebAppStart());
            await GoAsync(app);
            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();

            // Still unlocked on a page loaded afresh, so that what follows is the restart's doing
            // rather than a page load's.
            await GoAsync(app);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
            var cookie = await UnlockCookieAsync();
            Assert.That(cookie, Is.Not.Null, "the unlock left no cookie to carry across the restart");

            // Off the page first: left open, its circuit would notice the restart and reload the
            // page itself, racing the load below. The cookie stays with the browser.
            await Page.GotoAsync("about:blank");
            await app.RestartAsync();
            await GoAsync(app);

            await Assertions.Expect(Button("Locked")).ToBeVisibleAsync();
            await Assertions.Expect(Button("Add host")).ToBeHiddenAsync();
            Assert.That(await UnlockCookieAsync(), Is.EqualTo(cookie),
                "the browser no longer sends the cookie it unlocked with, so this proves nothing of the service");

            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
            await Assertions.Expect(Button("Add host")).ToBeVisibleAsync();
        }

        private async Task<string?> UnlockCookieAsync() =>
            (await Page.Context.CookiesAsync()).SingleOrDefault(c => c.Name == UnlockCookieName)?.Value;
    }
}
