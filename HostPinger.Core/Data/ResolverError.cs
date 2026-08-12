namespace HostPinger.Core.Data
{
    /// <summary>A single failed attempt to turn a host's address into an IP address.</summary>
    /// <remarks>
    /// Recorded against the address rather than against the host it is configured on. The address
    /// is what the resolver was asked about, it is unique across hosts, and a row that does not
    /// belong to a host survives that host being re-pointed or deleted — so the record of what
    /// failed is not rewritten by a later edit, and a name that was removed because it never
    /// resolved leaves the evidence of that behind. The page joins back to the host by address
    /// for as long as one still carries it.
    /// </remarks>
    public class ResolverError
    {
        /// <summary>
        /// How long a recorded failure is kept. The pruner deletes past it on every round, so this
        /// is also the widest window the page can count over — beyond it there is nothing left to
        /// count, and an address that has not failed inside it stops being listed at all.
        /// </summary>
        public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

        public long Id { get; set; }

        /// <summary>The address as configured on the host: the string handed to the resolver.</summary>
        public string Address { get; set; } = string.Empty;

        public DateTime TimestampUtc { get; set; }

        /// <summary>Which way the lookup failed.</summary>
        public ResolverFailure Reason { get; set; }
    }

    /// <summary>
    /// Why an address never became an IP address. The three are worth telling apart because they
    /// point at different things: a slow or unreachable resolver, a name that exists but carries no
    /// address, and a name that is simply not there.
    /// </summary>
    public enum ResolverFailure
    {
        /// <summary>The lookup did not come back inside the configured resolve timeout.</summary>
        TimedOut,

        /// <summary>The lookup came back, and there were no addresses in it.</summary>
        NoAddresses,

        /// <summary>The lookup failed outright: an unknown name, or no resolver able to answer.</summary>
        LookupFailed,
    }
}
