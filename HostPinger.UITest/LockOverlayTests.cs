using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The unlock overlay, driven the way a person drives it. Everything here needs a real browser:
    /// it is about what a click, a key and a page load do to a live circuit, none of which survives
    /// being reasoned about from the markup a request returns.
    /// </summary>
    [Parallelizable(ParallelScope.Self)]
    public class LockOverlayTests : BrowserTest
    {
        [Test]
        public async Task Overlay_ClosesOnEscape()
        {
            await GoAsync();
            await OpenOverlayAsync();

            await PasswordBox.PressAsync("Escape");

            await Assertions.Expect(Overlay).ToBeHiddenAsync();
        }

        [Test]
        public async Task Overlay_ClosesOnCancel()
        {
            await GoAsync();
            await OpenOverlayAsync();

            await Button(Dialog, "Cancel").ClickAsync();

            await Assertions.Expect(Overlay).ToBeHiddenAsync();
        }

        [Test]
        public async Task Overlay_ClosesOnAClickAway()
        {
            await GoAsync();
            await OpenOverlayAsync();

            // The very top of the overlay, which is backdrop rather than dialog.
            await Page.Mouse.ClickAsync(20, 20);

            await Assertions.Expect(Overlay).ToBeHiddenAsync();
        }

        [Test]
        public async Task Overlay_FocusesThePasswordBoxWhenItOpens()
        {
            await GoAsync();

            await OpenOverlayAsync();

            await Assertions.Expect(PasswordBox).ToBeFocusedAsync();
        }

        /// <summary>
        /// The password page is rendered statically, so its offer to unlock is a link rather than a
        /// click into the circuit. The marker that link carries has to open the overlay and then
        /// come off the address, or reloading would reopen it and a second attempt would have
        /// nothing left to change.
        /// </summary>
        [Test]
        public async Task Overlay_OpensFromThePasswordPageAndTakesItsMarkerBackOffTheAddress()
        {
            await GoAsync("/password");

            await Page.GetByRole(AriaRole.Link, new() { Name = "Unlock", Exact = true }).ClickAsync();

            await Assertions.Expect(Overlay).ToBeVisibleAsync();
            await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/password");
        }

        [Test]
        public async Task Overlay_UnlocksAndComesBackToThePageItWasOpenedOn()
        {
            await GoAsync("/configuration");
            await OpenOverlayAsync();

            await SubmitPasswordAsync(WebApp.Password);

            await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/configuration");
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
        }

        /// <summary>
        /// Unlocking in order to change the password is the one case that wants somewhere other
        /// than the page it was asked from.
        /// </summary>
        [Test]
        public async Task Overlay_TakesTheAskerWhereItWasGoing()
        {
            await GoAsync("/configuration");

            await ClickUntilAsync(Button("Unlock to change it"), Overlay);
            await SubmitPasswordAsync(WebApp.Password);

            await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/password");
            await Assertions.Expect(Page.Locator("#new-password")).ToBeVisibleAsync();
        }

        [Test]
        public async Task Overlay_ReopensWithItsErrorWhenThePasswordIsWrong()
        {
            await GoAsync();
            await OpenOverlayAsync();

            await SubmitPasswordAsync("not the password");

            await Assertions.Expect(Overlay).ToBeVisibleAsync();
            await Assertions.Expect(Page.GetByText("That is not the password.")).ToBeVisibleAsync();
            await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/");
        }

        /// <summary>
        /// Hiding the controls is a courtesy to the reader and refusing the change is the
        /// enforcement; this is the courtesy, which is the part a browser can see.
        /// </summary>
        [Test]
        public async Task HostsPage_OffersNothingToChangeUntilItIsUnlocked()
        {
            await GoAsync();

            await Assertions.Expect(Button("Add host")).ToBeHiddenAsync();

            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);

            await Assertions.Expect(Button("Add host")).ToBeVisibleAsync();
        }
    }
}
