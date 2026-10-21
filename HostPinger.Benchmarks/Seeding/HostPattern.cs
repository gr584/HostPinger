namespace HostPinger.Benchmarks.Seeding
{
    /// <summary>What one round comes out as for one host.</summary>
    internal enum RoundState : byte
    {
        Answered,

        Missed,

        /// <summary>
        /// The host's name did not resolve, so the round recorded a resolver error against its address
        /// and no attempt at all, which is what PingMonitorService does with an unresolved name.
        /// </summary>
        Unresolved,

        /// <summary>Not pinged: not added yet, paused, or the service was not running.</summary>
        NotPinged,
    }

    /// <summary>
    /// How a host is addressed, which decides what a network-wide outage does to it: a name stops
    /// resolving and leaves a gap with a resolver error in it, an IP address goes on being pinged and
    /// misses, and the loopback carries on answering.
    /// </summary>
    internal enum AddressKind
    {
        Loopback,
        Name,
        IpAddress,
    }

    /// <summary>
    /// One synthetic host: its address and how its round trips are spread, and the episodes that make
    /// up its history.
    /// </summary>
    /// <param name="Name">What the host is called, which names its pattern rather than any real machine.</param>
    /// <param name="Address">An address that cannot reach anything: a .invalid name, TEST-NET-1 or the loopback.</param>
    /// <param name="Kind">How <paramref name="Address"/> is reached; see <see cref="AddressKind"/>.</param>
    /// <param name="Roundtrip">The spread of its answered round trips.</param>
    /// <param name="Episodes">Its outages, absences and lost pings, in hours before the end of the history.</param>
    /// <param name="AddedHoursBeforeEnd">When it was added, or null for a host there from the start.</param>
    internal sealed record HostPattern(
        string Name,
        string Address,
        AddressKind Kind,
        RoundtripModel Roundtrip,
        IReadOnlyList<Episode> Episodes,
        double? AddedHoursBeforeEnd = null)
    {
        /// <summary>A host reached by a name in the reserved .invalid domain, which nothing will resolve.</summary>
        public static HostPattern ByName(string name, RoundtripModel roundtrip, params Episode[] episodes) =>
            new(name, $"{name}.bench.invalid", AddressKind.Name, roundtrip, episodes);

        /// <summary>A host reached by an address in TEST-NET-1, which is never routed.</summary>
        public static HostPattern ByIp(string name, int lastOctet, RoundtripModel roundtrip, params Episode[] episodes) =>
            new(name, $"192.0.2.{lastOctet}", AddressKind.IpAddress, roundtrip, episodes);
    }

    /// <summary>Something that happened to a host, placed in hours before the end of the history.</summary>
    internal abstract record Episode;

    /// <summary>A run of missed pings starting at a moment; still going at the end when Pings is null.</summary>
    internal sealed record Outage(double HoursBeforeEnd, int? Pings = null) : Episode;

    /// <summary>
    /// Missed pings at the same time every day, as a nightly job or a scheduled reboot leaves them,
    /// from <paramref name="Since"/> hours before the end up to <paramref name="Until"/>.
    /// </summary>
    internal sealed record Daily(
        double HoursBeforeEnd,
        int MinPings,
        int MaxPings,
        double Since = double.MaxValue,
        double Until = 0) : Episode;

    /// <summary>A stretch in which a host keeps dropping out for a few pings at a time.</summary>
    internal sealed record Flapping(double FromHoursBeforeEnd, double ToHoursBeforeEnd, int Runs, int MinPings, int MaxPings) : Episode;

    /// <summary>A stretch with no attempts at all, because the host was paused.</summary>
    internal sealed record Paused(double HoursBeforeEnd, TimeSpan For) : Episode;

    /// <summary>
    /// Pings lost one at a time, scattered at random between <paramref name="Since"/> and
    /// <paramref name="Until"/> hours before the end: packet loss. Where they fall matters as much as
    /// how many there are. The Hosts summary looks for a host's last downtime by stepping back over
    /// its missed pings, newest first, until it reaches a run long enough to count, so every lost
    /// ping after a host's last real downtime is a step of that walk; the windows put them where
    /// production had them.
    /// </summary>
    internal sealed record Scattered(int Runs, double Since = double.MaxValue, double Until = 0) : Episode;

    /// <summary>
    /// The spread of a host's answered round trips, as points on its cumulative distribution with
    /// straight lines between them: (0.5, 27) says half its answers took 27 ms or less. A first point
    /// at a fraction above zero holds its value up to that fraction, which is how a host on the local
    /// network answers most of its pings in under a millisecond, stored as zero; the line from there
    /// starts at one.
    /// </summary>
    /// <remarks>
    /// The values are taken from the production database's percentiles. They matter for more than
    /// looks: SQLite stores 0 and 1 in no bytes at all, anything up to 127 in one and the rest in two,
    /// and the answered-ping index carries the round trip, so the spread is part of what makes the
    /// file and its indexes the size they are.
    /// </remarks>
    internal sealed class RoundtripModel((double Cumulative, int Milliseconds)[] points)
    {
        public int Sample(Random random)
        {
            var u = random.NextDouble();
            for (var i = 0; i < points.Length; i++)
            {
                if (u >= points[i].Cumulative && i < points.Length - 1)
                {
                    continue;
                }

                if (i == 0)
                {
                    return points[0].Milliseconds;
                }

                var (fromFraction, fromValue) = points[i - 1];
                var (toFraction, toValue) = points[i];

                // Every answer under a millisecond is in the zeros the first point holds, so the line
                // above them starts at one; from zero, it would round a share of them back down to it.
                if (i == 1 && fromFraction > 0 && fromValue == 0)
                {
                    fromValue = 1;
                }

                var along = (u - fromFraction) / (toFraction - fromFraction);
                return (int)Math.Round(fromValue + (along * (toValue - fromValue)));
            }

            return points[^1].Milliseconds;
        }

        /// <summary>The points, for <see cref="SyntheticHistory.Fingerprint"/>.</summary>
        public override string ToString() =>
            string.Join(' ', points.Select(point => FormattableString.Invariant($"{point.Cumulative}:{point.Milliseconds}")));
    }
}
