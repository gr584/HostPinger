using Microsoft.AspNetCore.Authentication.Cookies;

namespace HostPinger.Security
{
    /// <summary>
    /// The cookie an unlock is made of. There are no accounts: signing in means holding the one
    /// password, and the cookie says nothing but that.
    /// </summary>
    internal static class UnlockCookie
    {
        /// <summary>
        /// How long an unlock lasts without the browser loading a page, renewed as it does. A copy
        /// taken off the network — the service is normally reached over plain HTTP — keeps working
        /// after Lock, which only clears the browser's own; ASP.NET's default of fourteen days left
        /// such a copy good for a fortnight, where this ends it in twelve hours unless it is used.
        /// Long enough that a browser in use through a working day is not locked partway through.
        /// </summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

        /// <summary>
        /// Deliberately not persistent, so closing the browser locks it again, and SameAsRequest
        /// rather than Always because the service is normally reached over plain HTTP — a cookie
        /// marked secure there would never come back.
        /// </summary>
        public static void Configure(CookieAuthenticationOptions options)
        {
            options.Cookie.Name = "hostpinger-unlock";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = Lifetime;
            options.SlidingExpiration = true;
            options.LoginPath = "/unlock";
            options.ReturnUrlParameter = "returnUrl";
        }
    }
}
