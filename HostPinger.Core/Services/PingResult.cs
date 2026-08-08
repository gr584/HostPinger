namespace HostPinger.Core.Services
{
    /// <summary>What became of one ping.</summary>
    public enum PingOutcome
    {
        /// <summary>The host replied, and the round trip is in <see cref="PingResult.RoundtripMs"/>.</summary>
        Answered,

        /// <summary>
        /// The ping was aimed at a known address and nothing came back, or the machine could not
        /// send it. Either way the host was there to be asked, so this is a missed ping.
        /// </summary>
        Unanswered,

        /// <summary>
        /// The address never became an IP, so nothing was ever asked of the host and nothing was
        /// learned about it. See <see cref="PingResult.IsRecordable"/>.
        /// </summary>
        Unresolved,
    }

    /// <summary>The outcome of one ping, with the round trip when there was one.</summary>
    /// <param name="Outcome">Which of the three things happened.</param>
    /// <param name="RoundtripMs">
    /// Round trip in milliseconds, or null unless <paramref name="Outcome"/> is
    /// <see cref="PingOutcome.Answered"/>.
    /// </param>
    public readonly record struct PingResult(PingOutcome Outcome, int? RoundtripMs = null)
    {
        public static PingResult Answered(int roundtripMs) => new(PingOutcome.Answered, roundtripMs);

        public static PingResult Unanswered { get; } = new(PingOutcome.Unanswered);

        public static PingResult Unresolved { get; } = new(PingOutcome.Unresolved);

        /// <summary>
        /// Whether this outcome says anything about the host that is worth storing. An unresolved
        /// address does not: storing it as a missed ping would read as an outage later, and invent
        /// one out of a name that does not resolve. The host is simply left out of the round.
        /// </summary>
        public bool IsRecordable => Outcome != PingOutcome.Unresolved;
    }
}
