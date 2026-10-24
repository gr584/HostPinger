namespace HostPinger.Security
{
    /// <summary>Where the unlock and lock forms are allowed to send a browser afterwards.</summary>
    internal static class LocalUrl
    {
        /// <summary>
        /// <paramref name="url"/> when it is a path inside this application, and the home page when
        /// it is anything else.
        /// </summary>
        /// <remarks>
        /// These values arrive on a query string or in a form field, both of which anyone can
        /// write. An absolute or protocol-relative URL would turn signing in into a redirect to
        /// somewhere else entirely, which is worth refusing here rather than at each of the three
        /// places that need to be sure of one.
        /// <para>
        /// A control character anywhere is refused as well. Browsers drop tabs and line breaks from
        /// a URL before reading it, so a slash, a tab and "/example.com" arrives as
        /// "//example.com" — somewhere else entirely, through a link the unlock page draws with the
        /// value as it came. ASP.NET's own IsLocalUrl refuses them for the same reason.
        /// </para>
        /// </remarks>
        public static string OrRoot(string? url) =>
            url is not null && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
                && !url.Any(char.IsControl)
                ? url
                : "/";
    }
}
