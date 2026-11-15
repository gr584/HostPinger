using System.Net;

namespace HostPinger.UITest
{
    /// <summary>
    /// The running application turning away requests under a name it does not answer to. Which
    /// names those are is settled in the unit tests; what only the real application can settle is
    /// that the filter stands in front of everything it serves, and that the names listed in its
    /// environment reach it. A page that has had its own name pointed at the service is what this
    /// stops, and such a page asks under that name for everything: the pages, the script, the
    /// circuit's connection.
    /// </summary>
    /// <remarks>
    /// No browser is driven: a browser cannot be told to send a name other than the one in its
    /// address bar, which is the point of the filter. This is a browser test all the same, because
    /// the application is only started for a run that has a browser.
    /// </remarks>
    public class HostNameFilterTests : BrowserTest
    {
        /// <summary>Everything the application serves is refused under a name it does not answer to.</summary>
        [TestCase("GET", "/")]
        [TestCase("GET", "/configuration")]
        [TestCase("GET", "/unlock")]
        [TestCase("GET", "/health")]
        [TestCase("GET", "/_framework/blazor.web.js")]
        [TestCase("GET", "/database-backup?token=none")]
        [TestCase("GET", "/no-such-page")]
        [TestCase("POST", "/_blazor/negotiate?negotiateVersion=1")]
        [TestCase("GET", "/_blazor")]
        public async Task ANameNotAnsweredTo_IsRefusedEverywhere(string method, string path)
        {
            using var response = await SendAsync(method, path, "attacker.example");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(body, Does.Contain("\"attacker.example\""), "the refusal names what was asked for");
                Assert.That(body, Does.Contain("Security__AllowedHostNames"), "and the setting that would allow it");
                Assert.That(body, Does.Not.Contain("<html"), "none of the application's own pages is rendered for it");
            });
        }

        /// <summary>
        /// The names a local network gives are answered under, with a port or without, and so are
        /// the ones this instance's environment lists.
        /// </summary>
        [TestCase("localhost")]
        [TestCase("monitor")]
        [TestCase("monitor.local")]
        [TestCase("monitor.lan:8080")]
        [TestCase("192.168.1.20:8080")]
        [TestCase("[::1]:8080")]
        [TestCase(WebApp.ListedHostName)]
        [TestCase("a.starred.example")]
        public async Task ANameAnsweredTo_GetsThePage(string host)
        {
            using var response = await SendAsync("GET", "/", host);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(body, Does.Contain("<html"));
            });
        }

        /// <summary>A name beside the listed ones is still refused: the list is a list, not a switch.</summary>
        [TestCase("other.listed.example")]
        [TestCase("starred.example")]
        public async Task ANameBesideTheListedOnes_IsRefused(string host)
        {
            using var response = await SendAsync("GET", "/", host);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        /// <summary>A refused name is in the log once, where whoever was turned away will look.</summary>
        [Test]
        public async Task ARefusedName_IsLoggedOnce()
        {
            const string Name = "logged-once.attacker.example";
            const string After = "logged-after.attacker.example";
            using (await SendAsync("GET", "/", Name))
            using (await SendAsync("GET", "/health", Name))
            using (await SendAsync("GET", "/", After))
            {
            }

            // The application's log reaches this process some time after the response does, in the
            // order it was written: once the later name's line is here, any second line for the
            // first would be here too.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (UiTestRun.LinesLogged(After) == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Multiple(() =>
            {
                Assert.That(UiTestRun.LinesLogged(After), Is.EqualTo(1), "the later refusal never reached the log");
                Assert.That(UiTestRun.LinesLogged(Name), Is.EqualTo(1));
            });
        }

        private static async Task<HttpResponseMessage> SendAsync(string method, string path, string host)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(new HttpMethod(method), $"{BaseUrl}{path}");
            request.Headers.Host = host;
            return await client.SendAsync(request);
        }
    }
}
