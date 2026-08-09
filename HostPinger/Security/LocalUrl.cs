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
        /// </remarks>
        public static string OrRoot(string? url) =>
            url is not null && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
                ? url
                : "/";
    }
}
