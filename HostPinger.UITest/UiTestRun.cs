using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The application and the browser, started once for the whole run and stopped at the end of
    /// it. Starting either per fixture would cost seconds apiece for no gain: nothing here shares
    /// state between tests except the application's stored password, which no test changes.
    /// </summary>
    /// <remarks>
    /// The browser is whichever Chrome or Chromium the machine already has. Playwright would rather
    /// download and pin its own, but that is a step of its own on a machine where browsers cannot
    /// be installed without a package manager; running against the browser that is there is worth
    /// more than being skipped for want of a download. Where there is none, the browser tests say
    /// so and are ignored rather than failing.
    /// </remarks>
    [SetUpFixture]
    public sealed class UiTestRun
    {
        private static readonly string[] CandidateBrowsers =
        [
            "/usr/bin/google-chrome",
            "/usr/bin/chromium-browser",
            "/usr/bin/chromium",
            "/snap/bin/chromium",
        ];

        private static WebApp? _app;
        private static IPlaywright? _playwright;
        private static IBrowser? _browser;

        /// <summary>The browser to drive, or null when this machine has none.</summary>
        public static string? BrowserPath { get; } = CandidateBrowsers.FirstOrDefault(File.Exists);

        public static string WhereItLooked => string.Join(", ", CandidateBrowsers);

        public static IBrowser Browser =>
            _browser ?? throw new InvalidOperationException("No browser was started for this run.");

        public static string BaseUrl =>
            _app?.BaseUrl ?? throw new InvalidOperationException("The application was not started for this run.");

        /// <summary>The database the running application reads, for a test that has to seed one.</summary>
        public static string DatabasePath =>
            _app?.DatabasePath ?? throw new InvalidOperationException("The application was not started for this run.");

        [OneTimeSetUp]
        public async Task StartAsync()
        {
            // Nothing to start when there is nothing to drive; the tests ignore themselves.
            if (BrowserPath is null)
            {
                return;
            }

            // Assertions that wait on the circuit — a redirect landing, a marker coming off the
            // address — need more than the five seconds Playwright allows by default.
            Assertions.SetDefaultExpectTimeout(15_000);

            _app = await WebApp.StartAsync();
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                ExecutablePath = BrowserPath,
            });
        }

        [OneTimeTearDown]
        public async Task StopAsync()
        {
            if (_browser is not null)
            {
                await _browser.DisposeAsync();
            }

            _playwright?.Dispose();

            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }
}
