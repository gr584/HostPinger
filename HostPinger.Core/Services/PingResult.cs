using HostPinger.Core.Data;

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
        /// learned about it. Kept out of the ping history and recorded as a
        /// <see cref="ResolverError"/> instead; see <see cref="PingResult.IsRecordablePing"/>.
        /// </summary>
        Unresolved,
    }

    /// <summary>The outcome of one ping, with the round trip when there was one.</summary>
    /// <param name="Outcome">Which of the three things happened.</param>
    /// <param name="RoundtripMs">
    /// Round trip in milliseconds, or null unless <paramref name="Outcome"/> is
    /// <see cref="PingOutcome.Answered"/>.
    /// </param>
    /// <param name="Failure">
    /// Which way the lookup failed, or null unless <paramref name="Outcome"/> is
    /// <see cref="PingOutcome.Unresolved"/>.
    /// </param>
    public readonly record struct PingResult(
        PingOutcome Outcome,
        int? RoundtripMs = null,
        ResolverFailure? Failure = null)
    {
        public static PingResult Answered(int roundtripMs) => new(PingOutcome.Answered, roundtripMs);

        public static PingResult Unanswered { get; } = new(PingOutcome.Unanswered);

        /// <summary>An address that never resolved, carrying which way the lookup failed.</summary>
        public static PingResult Unresolved(ResolverFailure failure) =>
            new(PingOutcome.Unresolved, Failure: failure);

        /// <summary>
        /// Whether this outcome belongs in the ping history. An unresolved address does not:
        /// storing it as a missed ping would read as an outage later, and invent one out of a name
        /// that does not resolve. The host is left out of the round and the failed lookup is
        /// recorded as a <see cref="ResolverError"/> instead, which is where it can be read without
        /// being mistaken for the host being down.
        /// </summary>
        public bool IsRecordablePing => Outcome != PingOutcome.Unresolved;
    }
}
