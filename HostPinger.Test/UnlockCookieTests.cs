using HostPinger.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace HostPinger.Test
{
    /// <summary>
    /// The cookie an unlock is made of. Lock only clears the browser's own copy, so how long a
    /// copy taken off the network stays good is the cookie's lifetime, which ASP.NET would leave at
    /// fourteen days, renewed on use.
    /// </summary>
    public class UnlockCookieTests
    {
        [Test]
        public void Configure_EndsAnUnlockTwelveHoursAfterItWasLastUsed()
        {
            var options = new CookieAuthenticationOptions();

            UnlockCookie.Configure(options);

            Assert.Multiple(() =>
            {
                Assert.That(options.ExpireTimeSpan, Is.EqualTo(TimeSpan.FromHours(12)));
                Assert.That(options.SlidingExpiration, Is.True, "a browser in use stays unlocked");
            });
        }

        /// <summary>What the cookie was before it had a lifetime of its own, kept as it was.</summary>
        [Test]
        public void Configure_KeepsTheCookieHttpOnlyLaxAndOnTheRequestsScheme()
        {
            var options = new CookieAuthenticationOptions();

            UnlockCookie.Configure(options);

            Assert.Multiple(() =>
            {
                Assert.That(options.Cookie.Name, Is.EqualTo("hostpinger-unlock"));
                Assert.That(options.Cookie.HttpOnly, Is.True);
                Assert.That(options.Cookie.SameSite, Is.EqualTo(SameSiteMode.Lax));
                Assert.That(options.Cookie.SecurePolicy, Is.EqualTo(CookieSecurePolicy.SameAsRequest));
                Assert.That(options.LoginPath.Value, Is.EqualTo("/unlock"));
                Assert.That(options.ReturnUrlParameter, Is.EqualTo("returnUrl"));
            });
        }
    }
}
