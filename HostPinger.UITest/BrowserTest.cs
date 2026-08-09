using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// A page of its own for each test, in a browser context of its own — which is what makes every
    /// test start out locked, since an unlock is a cookie and a fresh context has none.
    /// </summary>
    public abstract class BrowserTest
    {
        private IBrowserContext _context = null!;

        protected IPage Page { get; private set; } = null!;

        protected static string BaseUrl => UiTestRun.BaseUrl;

        [SetUp]
        public async Task OpenPageAsync()
        {
            if (UiTestRun.BrowserPath is null)
            {
                Assert.Ignore(
                    "No Chrome or Chromium on this machine, so the browser tests cannot run. "
                    + $"Looked in: {UiTestRun.WhereItLooked}.");
            }

            _context = await UiTestRun.Browser.NewContextAsync();
            Page = await _context.NewPageAsync();
        }

        [TearDown]
        public async Task ClosePageAsync()
        {
            if (_context is not null)
            {
                await _context.DisposeAsync();
            }
        }

        /// <summary>How long a page is given to connect its circuit before the test gives up.</summary>
        private static readonly TimeSpan CircuitTimeout = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Opens a page and waits until it is live, rather than merely drawn.
        /// </summary>
        /// <remarks>
        /// A Blazor Server page arrives complete and inert: every control is in the markup, and
        /// none of them does anything until the browser has opened the WebSocket its circuit runs
        /// over. A click in that window is not an error — it is simply ignored — so waiting for
        /// that socket is what makes a test's first click mean anything. Every page here carries
        /// the layout's lock control, which is interactive, so every page connects one.
        /// </remarks>
        protected async Task GoAsync(string path = "/")
        {
            var connected = new TaskCompletionSource();
            void Opened(object? sender, IWebSocket socket)
            {
                // The application's own traffic; a page can open sockets of its own.
                if (socket.Url.Contains("/_blazor"))
                {
                    connected.TrySetResult();
                }
            }

            Page.WebSocket += Opened;
            try
            {
                await Page.GotoAsync($"{BaseUrl}{path}");
                await connected.Task.WaitAsync(CircuitTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"{path} never connected a circuit, so nothing on it would have answered a "
                    + "click. Either the framework script did not load — see the note on the "
                    + "environment in WebApp — or the page carries no interactive component.");
            }
            finally
            {
                Page.WebSocket -= Opened;
            }
        }

        /// <summary>The overlay's backdrop, which is present exactly while the overlay is open.</summary>
        protected ILocator Overlay => Page.Locator(".modal-backdrop");

        /// <summary>
        /// The overlay's own box. Buttons are looked for inside it rather than on the page, because
        /// the pages carry their own Unlock and Cancel and a locator matching two things fails.
        /// </summary>
        protected ILocator Dialog => Page.Locator(".modal-content");

        protected ILocator PasswordBox => Page.Locator("#unlock-password");

        /// <summary>
        /// A button by its exact name. Playwright matches a role's name as a case-insensitive
        /// substring unless told otherwise, which would have "Unlock" pick out "Unlocked" and
        /// "Unlock to change it" as well.
        /// </summary>
        protected static ILocator Button(ILocator within, string name) =>
            within.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = name, Exact = true });

        protected ILocator Button(string name) =>
            Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = true });

        /// <summary>
        /// Clicks something until it has the effect it is supposed to have.
        /// </summary>
        /// <remarks>
        /// <see cref="GoAsync"/> has already waited for the circuit's socket, which is the last
        /// moment a test can observe; the components behind the markup are attached a beat after
        /// it, and that beat is not observable at all. This covers it. Every button used this way
        /// opens something, so a click that did land and one that did not are both safe to repeat,
        /// and a click that never takes gives up rather than hanging.
        /// </remarks>
        protected static async Task ClickUntilAsync(ILocator control, ILocator effect)
        {
            for (var attempt = 0; ; attempt++)
            {
                await control.ClickAsync();
                try
                {
                    await effect.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 2_000,
                    });
                    return;
                }
                catch (TimeoutException) when (attempt < 9)
                {
                }
            }
        }

        /// <summary>Opens the overlay from the button in the top right and waits for it.</summary>
        protected Task OpenOverlayAsync() => ClickUntilAsync(Button("Locked"), Overlay);

        /// <summary>Types the password into the open overlay and submits it.</summary>
        protected async Task SubmitPasswordAsync(string password)
        {
            await PasswordBox.FillAsync(password);
            await Button(Dialog, "Unlock").ClickAsync();
        }
    }
}
