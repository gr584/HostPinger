namespace HostPinger.UITest
{
    /// <summary>How a test's own <see cref="WebApp"/> is started; the defaults are the run's.</summary>
    public sealed record WebAppStart
    {
        /// <summary>
        /// Writes the database at the path it is given before the start, in place of
        /// <see cref="WebApp.SeedSettings"/>.
        /// </summary>
        public Action<string>? PrepareDatabase { get; init; }

        /// <summary>What /health says once the start has got as far as the test needs: its stage.</summary>
        public string ReadyAt { get; init; } = "Healthy";

        /// <summary>More environment variables for the application.</summary>
        public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    }
}
