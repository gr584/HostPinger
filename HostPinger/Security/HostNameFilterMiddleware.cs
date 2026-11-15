namespace HostPinger.Security
{
    /// <summary>
    /// Turns away, ahead of everything else, a request whose Host header names something
    /// <see cref="HostNameFilter"/> does not answer to: the pages, the circuit's connection, the
    /// backup download and <c>/health</c> alike.
    /// </summary>
    internal sealed class HostNameFilterMiddleware(
        RequestDelegate next,
        HostNameFilter filter,
        ILogger<HostNameFilterMiddleware> logger)
    {
        /// <summary>
        /// How many refused names are remembered as already logged. Past it nothing new is logged,
        /// so that requests under ever-changing names cannot fill the journal.
        /// </summary>
        internal const int MaximumLogged = 32;

        private readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);

        public Task InvokeAsync(HttpContext context)
        {
            var host = context.Request.Host.Host;
            if (filter.Allows(host))
            {
                return next(context);
            }

            LogOnce(host, context);

            // Written here rather than left to the status code pages, which would render the
            // application's own layout for a name it has just refused to be.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync(HostNameFilter.Refusal(host), context.RequestAborted);
        }

        /// <summary>
        /// One line per name, which is what somebody who has just been turned away under their
        /// own name needs to find, and no more than that of anybody trying names.
        /// </summary>
        private void LogOnce(string host, HttpContext context)
        {
            var name = HostNameFilter.Printable(host);
            lock (_logged)
            {
                if (_logged.Count >= MaximumLogged || !_logged.Add(name))
                {
                    return;
                }
            }

            logger.LogWarning(
                "Refused a request from {Address} for the name {Host}, which this service does not answer to. "
                + "If it should, add the name to {Variable} in the service's environment and restart it. "
                + "Further requests under that name are refused without another line here.",
                RequestOrigin.Describe(context),
                name,
                HostNameFilter.EnvironmentVariable);
        }
    }
}
