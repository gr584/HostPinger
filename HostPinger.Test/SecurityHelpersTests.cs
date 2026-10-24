using System.Net;
using HostPinger.Security;
using Microsoft.AspNetCore.Http;

namespace HostPinger.Test
{
    /// <summary>
    /// The small pieces the unlock and lock pages lean on: where they may send a browser
    /// afterwards, how they name a request in the log, and how a page asks for the overlay.
    /// </summary>
    public class SecurityHelpersTests
    {
        /// <summary>A path inside the application, query and all, is where the browser goes.</summary>
        [TestCase("/")]
        [TestCase("/configuration")]
        [TestCase("/graph/3?from=2026-10-01T00:00:00Z")]
        public void LocalUrl_KeepsAPathInsideTheApplication(string url)
        {
            Assert.That(LocalUrl.OrRoot(url), Is.EqualTo(url));
        }

        /// <summary>
        /// Anything that could lead off the site — an absolute address, a protocol-relative one,
        /// the backslash form browsers read the same way, one a browser makes protocol-relative by
        /// dropping a tab or a line break — or nothing at all goes to the home page, so signing in
        /// cannot be turned into a redirect somewhere else.
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("https://example.com/")]
        [TestCase("//example.com/")]
        [TestCase("/\\example.com/")]
        [TestCase("/\t/example.com/")]
        [TestCase("/\n/example.com/")]
        [TestCase("/\r/example.com/")]
        [TestCase("configuration")]
        [TestCase("javascript:alert(1)")]
        public void LocalUrl_SendsAnythingElseHome(string? url)
        {
            Assert.That(LocalUrl.OrRoot(url), Is.EqualTo("/"));
        }

        /// <summary>The connection's own address, which is the one hop the service can actually see.</summary>
        [Test]
        public void RequestOrigin_NamesTheAddressTheConnectionCameFrom()
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

            Assert.That(RequestOrigin.Describe(context), Is.EqualTo("192.168.1.20"), "a header anyone can set is not taken on trust");
        }

        /// <summary>No request, or one with no address, is said to be from an unknown one rather than throwing.</summary>
        [Test]
        public void RequestOrigin_SaysWhenItCannotTell()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RequestOrigin.Describe((HttpContext?)null), Is.EqualTo("an unknown address"));
                Assert.That(RequestOrigin.Describe(new DefaultHttpContext()), Is.EqualTo("an unknown address"));
            });
        }

        /// <summary>A page's request reaches the overlay listening on the same page, with where to go afterwards.</summary>
        [Test]
        public void LockPrompt_PassesTheRequestToWhoeverIsListening()
        {
            var prompt = new LockPrompt();
            var heard = new List<string?>();
            prompt.Requested += heard.Add;

            prompt.Request();
            prompt.Request("/password");

            Assert.That(heard, Is.EqualTo(new[] { null, "/password" }));
        }

        /// <summary>Asking with nobody listening — the overlay not drawn yet — is not an error.</summary>
        [Test]
        public void LockPrompt_AskingWithNobodyListeningDoesNothing()
        {
            Assert.That(() => new LockPrompt().Request(), Throws.Nothing);
        }
    }
}
