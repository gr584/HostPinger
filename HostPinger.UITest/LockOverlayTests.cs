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

        /// <summary>
        /// One wrong password costs nothing: the first few are free, which is what keeps somebody
        /// mistyping their own password from meeting the wait that
        /// <see cref="Overlay_StopsLookingAtGuessesOnceThereHaveBeenTooManyWrongOnes"/> is about.
        /// </summary>
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
        /// The rising wait, driven through the form it protects. The schedule itself is settled in
        /// PasswordAttemptsTests against a clock that can be moved by hand; what only a browser can
        /// show is that the form is wired to it at all, and that the wait finds its way back into
        /// the overlay — which it does the long way round, as a count of seconds on the address.
        /// </summary>
        /// <remarks>
        /// Every test in this fixture reaches the application from the same address, so they share
        /// one tally, and the tests run one after another rather than at once. That is what makes
        /// this safe to do here and what obliges it to sit the wait out afterwards rather than
        /// leaving it standing: a run that unlocks in three other places must not find itself
        /// turned away in them.
        /// </remarks>
        [Test]
        public async Task Overlay_StopsLookingAtGuessesOnceThereHaveBeenTooManyWrongOnes()
        {
            await GoAsync();
            await OpenOverlayAsync();

            // However many wrong answers are free, rather than a number written down twice: the
            // point is that there is an end to them, not where exactly it falls.
            var refused = Page.GetByText("Too many wrong passwords");
            for (var attempt = 1; !await refused.IsVisibleAsync(); attempt++)
            {
                Assert.That(attempt, Is.LessThan(10), "no run of wrong passwords was ever refused");
                await SubmitPasswordAsync("not the password");
                await Assertions.Expect(Overlay).ToBeVisibleAsync();
                await Assertions.Expect(Page).ToHaveURLAsync($"{BaseUrl}/");
            }

            // Nothing typed now would be looked at, so there is nothing to send it with.
            var unlock = Button(Dialog, "Unlock");
            await Assertions.Expect(unlock).ToBeDisabledAsync();

            // Counting down rather than merely saying a number: what it said a second ago is not
            // what it says now. Read twice rather than waited for, because what it starts at
            // depends on where in the schedule this run has landed, and an assertion that waits for
            // a particular reading would pass without ever having compared two.
            var notice = Dialog.Locator(".alert");
            var atFirst = await notice.InnerTextAsync();
            await Task.Delay(1_500);
            Assert.That(await notice.InnerTextAsync(), Is.Not.EqualTo(atFirst), "it is not counting down");

            // And the end of it is an invitation rather than something to keep trying at. Given
            // longer than the first wait in the schedule, so that a run which somehow arrived here
            // further along it reports what it found rather than how long it was given.
            var timeout = new LocatorAssertionsToBeVisibleOptions { Timeout = 90_000 };
            await Assertions.Expect(Page.GetByText("You can try again now.")).ToBeVisibleAsync(timeout);
            await Assertions.Expect(unlock).ToBeEnabledAsync();

            // Which it is: the same password that was turned away a moment ago now works. That also
            // leaves nothing owed against the address the rest of the run shares.
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
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
