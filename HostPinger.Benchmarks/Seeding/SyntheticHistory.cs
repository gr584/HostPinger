using System.Security.Cryptography;
using System.Text;

namespace HostPinger.Benchmarks.Seeding
{
    /// <summary>
    /// The history the benchmark database is seeded with: twenty-one hosts pinged every ten seconds for
    /// forty-eight days, about 8.3 million attempts, laid out to the pattern of the production database
    /// that the page freezes were measured against in October 2026.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the shape is taken from that database: how many hosts and how they are addressed, the
    /// interval and retry count, how long its history ran, and per host the spread of round trips and
    /// the count, length and timing of its outages. None of its names or addresses are. Each host here
    /// is named for its pattern instead, so a benchmark database opened in the application says what
    /// it is.
    /// </para>
    /// <para>
    /// What made that database slow is all here. Two hosts are in outages that are still going at the
    /// end: one for 41 days and one for 20, after a 16-day pause. Those are what put the Hosts summary
    /// at seconds before the answered-ping index, and what opening the graph around a downtime reads
    /// end to end. One host has five outages of nine to twenty-nine hours that ended, one sleeps most
    /// of every day, and two flap for hours at a time. Seven network-wide outages hit every host at
    /// once: the hosts addressed by IP miss their pings, while the names stop resolving and leave gaps.
    /// Beyond that, single lost pings are scattered where production had them, a service restart
    /// leaves a gap across all hosts, and one host was added two weeks before the end.
    /// </para>
    /// <para>
    /// One thing is added rather than copied: the resolver errors. The production copy held none,
    /// most likely because it sat at its size limit, where every pruning pass deletes the oldest
    /// thousand of them along with the oldest attempts. Its gaps are where the current build records
    /// one per name and round, so those are written for the gaps inside the thirty days kept, which
    /// gives the Resolver errors page the table the outages would leave it.
    /// </para>
    /// <para>
    /// Everything is deterministic. The randomness that scatters the lost pings and spreads the round
    /// trips comes from a fixed seed per host, so the same history seeds the same database byte for
    /// byte, and two runs of a benchmark read the same rows. The cached template is named for a hash
    /// of what is planned, see <see cref="Fingerprint"/>; a change to how it is written rather than to
    /// what is planned bumps <see cref="Version"/>.
    /// </para>
    /// </remarks>
    internal static class SyntheticHistory
    {
        /// <summary>Part of the template's fingerprint, for changes the plans themselves do not show.</summary>
        public const int Version = 1;

        public const int IntervalSeconds = 10;

        /// <summary>
        /// What production ran with. One retry makes two missed pings a downtime, which is what most of
        /// the flapping here is counted against.
        /// </summary>
        public const int RetryAttempts = 1;

        public const int TimeoutSeconds = 5;

        public const int ResolveTimeoutSeconds = 3;

        /// <summary>How far back the history reaches: forty-eight days and eight hours, as production's did.</summary>
        public const int HistoryHours = 1160;

        public const int RoundsPerHour = 3600 / IntervalSeconds;

        /// <summary>Every round from the first to the last, both included.</summary>
        public const int RoundCount = (HistoryHours * RoundsPerHour) + 1;

        /// <summary>
        /// How many missed pings the Hosts summary steps over, across all hosts, to find each one's
        /// last downtime in production's database; see <see cref="SummaryWalk"/>. Since the
        /// answered-ping index this walk is the part of that query's cost that history decides, so
        /// the seeding is held to it.
        /// </summary>
        public const int ProductionSummaryWalk = 348;

        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(IntervalSeconds);

        /// <summary>
        /// The last round. Fixed rather than taken from the clock, so the database is the same whenever
        /// it is seeded; the benchmarks measure against this, never against today. The fraction of a
        /// second is there because a real round is stamped with the moment it ran, and the stored
        /// timestamps are as long as production's — they are in every index on the attempts.
        /// </summary>
        public static readonly DateTime LastRoundUtc =
            new DateTime(2026, 10, 10, 18, 0, 0, DateTimeKind.Utc).AddTicks(4_375_081);

        /// <summary>
        /// The network-wide outages, as hours before the end and how long each lasted. The two short
        /// pairs were a minute or two each; the last was a gateway down for most of two hours in three
        /// stretches.
        /// </summary>
        public static readonly (double HoursBeforeEnd, TimeSpan Length)[] NetworkOutages =
        [
            (984.98, TimeSpan.FromSeconds(100)),
            (984.78, TimeSpan.FromSeconds(80)),
            (674.33, TimeSpan.FromSeconds(90)),
            (673.30, TimeSpan.FromSeconds(90)),
            (160.62, TimeSpan.FromSeconds(700)),
            (160.41, TimeSpan.FromSeconds(1540)),
            (159.98, TimeSpan.FromSeconds(4020)),
        ];

        /// <summary>The service being restarted: no host is pinged for its length.</summary>
        public static readonly (double HoursBeforeEnd, TimeSpan Length)[] ServiceStops =
        [
            (1025.13, TimeSpan.FromSeconds(89)),
        ];

        /// <summary>
        /// Down from 982 hours before the end to the end: the longest outage, and the host the Hosts
        /// page's downtime link and the delete benchmarks are pointed at.
        /// </summary>
        public const string LongestOutage = "server-down-41d";

        /// <summary>
        /// The hosts a graph is opened on when a benchmark plots several: eight, the most the graph
        /// draws at once, picked to take in every kind of history, the two still down included.
        /// </summary>
        public static readonly IReadOnlyList<string> ChartedHosts =
        [
            "loopback",
            "internet-a",
            "lan-nightly",
            "wifi-lossy",
            "lan-long-outages",
            "laptop-down-20d",
            LongestOutage,
            "wifi-new",
        ];

        /// <summary>
        /// The hosts, in the order they are added, which is their id order. Their round trips are
        /// production's percentiles: first, median, 90th and 99th, with the rare spikes towards its
        /// maximum. Every run of two or more missed pings is one production had, by when it started and
        /// how many pings it lasted, apart from the hours of flapping, which are laid out as that many
        /// runs over the same stretch. The single lost pings are scattered over the stretches
        /// production had them in.
        /// </summary>
        /// <remarks>
        /// A few runs sit a minute or two away from where production had them. On a host reached by
        /// name, production recorded misses either side of a network-wide outage's gap, and here the
        /// gap would swallow them, so they are moved to just before or just after it.
        /// </remarks>
        public static readonly IReadOnlyList<HostPattern> Hosts =
        [
            new HostPattern(
                "loopback",
                "127.0.0.1",
                AddressKind.Loopback,
                new RoundtripModel([(0.997, 0), (0.9999, 1), (1, 13)]),
                []),

            HostPattern.ByName(
                "internet-a",
                new RoundtripModel([(0, 9), (0.01, 11), (0.5, 27), (0.9, 38), (0.99, 47), (0.9995, 120), (1, 777)]),
                new Outage(1045.92, 2),
                new Outage(1024.50, 2),
                new Outage(985.05, 7),
                new Outage(969.45, 2),
                new Outage(703.32, 2),
                new Outage(674.40, 8),
                new Outage(160.70, 8),
                new Outage(158.85, 28),
                new Outage(70.02, 2),
                new Scattered(79, Until: 70.1),
                new Scattered(32, Since: 70.0)),

            HostPattern.ByName(
                "internet-b",
                new RoundtripModel([(0, 15), (0.01, 21), (0.5, 45), (0.9, 69), (0.99, 76), (0.9995, 200), (1, 2096)]),
                new Outage(985.05, 8),
                new Outage(674.40, 8),
                new Outage(672.91, 2),
                new Outage(534.19, 2),
                new Outage(228.07, 2),
                new Outage(160.80, 58),
                new Outage(158.85, 13),
                new Outage(145.04, 2),
                new Scattered(40, Until: 145.1),
                new Scattered(2, Since: 145.0)),

            HostPattern.ByName(
                "lan-flapping",
                new RoundtripModel([(0.506, 0), (0.75, 1), (0.9, 3), (0.99, 7), (0.9999, 40), (1, 2899)]),
                new Flapping(1099.3, 1093.9, 44, 2, 4),
                new Outage(1099.28, 18),
                new Outage(1097.16, 32),
                new Outage(1096.79, 13),
                new Outage(1096.49, 85),
                new Outage(1094.70, 16),
                new Outage(1094.31, 6),
                new Outage(1084.25, 2),
                new Outage(1049.99, 3),
                new Outage(166.68, 2),
                new Outage(158.85, 2),
                new Scattered(17, Until: 158.9)),

            HostPattern.ByName(
                "lan-steady",
                new RoundtripModel([(0.556, 0), (0.8, 1), (0.9, 3), (0.99, 8), (1, 43)]),
                new Outage(784.45, 3),
                new Outage(729.25, 2),
                new Outage(411.95, 2)),

            HostPattern.ByName(
                "lan-nightly",
                new RoundtripModel([(0.489, 0), (0.5, 1), (0.9, 4), (0.99, 8), (1, 38)]),
                new Daily(6.02, 3, 4),
                new Outage(318.01, 12),
                new Outage(150.01, 12),
                new Outage(102.01, 12),
                new Outage(809.34, 1909)),

            HostPattern.ByName(
                "wifi-lossy",
                new RoundtripModel([(0, 1), (0.5, 6), (0.9, 216), (0.99, 312), (0.9999, 900), (1, 4725)]),
                new Daily(7.02, 3, 3, Since: 1160, Until: 895),
                new Outage(972.50, 2),
                new Outage(878.39, 3),
                new Outage(878.14, 368),
                new Outage(877.10, 41),
                new Outage(868.24, 3),
                new Outage(855.57, 3),
                new Outage(842.79, 3),
                new Outage(837.13, 2),
                new Outage(834.39, 2),
                new Outage(809.35, 9),
                new Paused(809.32, TimeSpan.FromSeconds(7220)),
                new Outage(663.52, 3),
                new Outage(663.47, 2),
                new Outage(659.35, 5),
                new Outage(591.54, 18),
                new Outage(579.57, 10),
                new Outage(573.64, 15),
                new Outage(534.58, 2),
                new Outage(530.21, 4),
                new Flapping(503.0, 502.6, 8, 2, 4),
                new Outage(372.10, 2),
                new Outage(359.34, 38),
                new Flapping(310.2, 307.8, 5, 2, 3),
                new Outage(293.25, 5),
                new Outage(289.19, 2),
                new Outage(285.33, 2),
                new Outage(280.93, 26),
                new Outage(280.55, 2),
                new Outage(166.05, 2),
                new Outage(158.85, 5),
                new Scattered(1202, Until: 158.9),
                new Scattered(148, Since: 158.8)),

            HostPattern.ByName(
                "internet-c",
                new RoundtripModel([(0, 1), (0.5, 49), (0.9, 104), (0.99, 117), (0.9995, 250), (1, 770)]),
                new Outage(878.39, 5),
                new Outage(727.10, 2),
                new Outage(723.11, 2),
                new Outage(531.82, 23),
                new Outage(531.74, 2),
                new Outage(234.01, 16),
                new Outage(233.93, 2),
                new Outage(158.85, 5),
                new Scattered(159, Until: 158.9),
                new Scattered(28, Since: 158.8)),

            HostPattern.ByName(
                "lan-monthly-reboot",
                new RoundtripModel([(0.887, 0), (0.95, 1), (0.99, 6), (0.9999, 30), (1, 2591)]),
                new Outage(943.00, 12),
                new Outage(790.43, 12),
                new Outage(790.34, 12),
                new Outage(223.00, 12),
                new Scattered(1)),

            HostPattern.ByName(
                "lan-long-outages",
                new RoundtripModel([(0.904, 0), (0.95, 1), (0.99, 5), (0.9999, 40), (1, 2977)]),
                new Outage(1125.40, 9561),
                new Outage(753.20, 3),
                new Outage(411.28, 4301),
                new Outage(397.07, 5779),
                new Outage(362.70, 3120),
                new Outage(189.24, 10311),
                new Scattered(24)),

            // A machine that is off for most of each day, then paused for sixteen days, then back for
            // three weeks before going down for good.
            HostPattern.ByName(
                "laptop-down-20d",
                new RoundtripModel([(0, 1), (0.5, 9), (0.9, 231), (0.99, 297), (0.9999, 900), (1, 4128)]),
                new Outage(1160.06, 5993),
                new Outage(1143.35, 3),
                new Outage(1140.04, 7574),
                new Outage(1117.23, 4),
                new Outage(1116.97, 3),
                new Outage(1116.88, 7991),
                new Outage(1091.96, 8406),
                new Outage(1067.36, 7450),
                new Outage(1046.65, 22582),
                new Paused(983.86, TimeSpan.FromSeconds(1_413_636)),
                new Outage(562.99, 7528),
                new Outage(539.76, 6534),
                new Outage(521.54, 3),
                new Outage(521.48, 10),
                new Flapping(518.0, 515.8, 4, 2, 2),
                new Outage(515.26, 4007),
                new Flapping(504.1, 503.0, 9, 2, 3),
                new Outage(494.38),
                new Scattered(42)),

            HostPattern.ByIp("ip-1", 11, new RoundtripModel([(0.226, 0), (0.5, 1), (0.9, 3), (0.99, 6), (0.9999, 40), (1, 2035)])),

            HostPattern.ByIp("ip-2", 12, new RoundtripModel([(0.007, 0), (0.5, 6), (0.9, 10), (0.99, 18), (0.9999, 60), (1, 2046)])),

            // Flapping every couple of minutes for seven hours, then a long outage, then down for good.
            HostPattern.ByName(
                LongestOutage,
                new RoundtripModel([(0.713, 0), (0.9, 1), (0.99, 6), (0.9999, 40), (1, 1984)]),
                new Flapping(1141.41, 1134.27, 138, 2, 3),
                new Outage(996.36, 458),
                new Outage(995.05, 3),
                new Outage(981.93),
                new Scattered(13)),

            HostPattern.ByIp("ip-3", 13, new RoundtripModel([(0.008, 0), (0.5, 2), (0.9, 4), (0.99, 17), (0.9999, 60), (1, 2038)])),

            HostPattern.ByIp(
                "ip-4",
                14,
                new RoundtripModel([(0.082, 0), (0.5, 1), (0.9, 3), (0.99, 9), (1, 109)]),
                new Outage(878.39, 3),
                new Outage(868.18, 3),
                new Outage(868.16, 18)),

            HostPattern.ByIp(
                "ip-5",
                15,
                new RoundtripModel([(0.096, 0), (0.5, 1), (0.9, 3), (0.99, 9), (1, 109)]),
                new Outage(878.39, 4),
                new Outage(878.33, 3),
                new Outage(878.30, 18)),

            HostPattern.ByIp(
                "ip-6",
                16,
                new RoundtripModel([(0.122, 0), (0.5, 1), (0.9, 3), (0.99, 9), (1, 109)]),
                new Outage(878.39, 3),
                new Outage(878.27, 3),
                new Outage(878.22, 18)),

            HostPattern.ByIp(
                "ip-7",
                17,
                new RoundtripModel([(0.16, 0), (0.5, 1), (0.9, 3), (0.99, 9), (1, 70)]),
                new Outage(878.39, 3),
                new Outage(868.82, 3),
                new Outage(868.80, 18)),

            HostPattern.ByName(
                "lan-hour-outage",
                new RoundtripModel([(0.613, 0), (0.8, 1), (0.9, 3), (0.99, 10), (1, 67)]),
                new Outage(169.63, 357),
                new Outage(158.85, 11)),

            // Added two weeks before the end, and losing pings in bursts, most of all on the last day.
            HostPattern.ByName(
                "wifi-new",
                new RoundtripModel([(0, 1), (0.5, 4), (0.9, 10), (0.99, 21), (0.9995, 80), (1, 776)]),
                new Outage(250.11, 3),
                new Outage(224.99, 61),
                new Outage(223.95, 2),
                new Outage(175.29, 24),
                new Outage(175.10, 2),
                new Outage(172.19, 2),
                new Outage(171.77, 2),
                new Outage(171.73, 2),
                new Outage(171.69, 2),
                new Outage(171.60, 2),
                new Outage(171.23, 2),
                new Outage(171.16, 2),
                new Outage(170.73, 2),
                new Outage(170.28, 2),
                new Outage(158.85, 4),
                new Outage(146.80, 2),
                new Outage(142.79, 4),
                new Outage(92.89, 2),
                new Outage(26.88, 2),
                new Outage(26.78, 2),
                new Outage(21.99, 3),
                new Scattered(100, Since: 358, Until: 168),
                new Scattered(68, Since: 168, Until: 72),
                new Scattered(74, Since: 72, Until: 22.1),
                new Scattered(115, Since: 21.9)) with { AddedHoursBeforeEnd = 358.0 },
        ];

        /// <summary>When a round ran.</summary>
        public static DateTime RoundTimeUtc(int round) => LastRoundUtc - (Interval * (RoundCount - 1 - round));

        /// <summary>The round nearest a moment given in hours before the end, held inside the history.</summary>
        public static int RoundAt(double hoursBeforeEnd)
        {
            var roundsBack = Math.Round(hoursBeforeEnd * RoundsPerHour);
            return roundsBack >= RoundCount - 1 ? 0
                : roundsBack <= 0 ? RoundCount - 1
                : RoundCount - 1 - (int)roundsBack;
        }

        /// <summary>
        /// Lays out every round of one host's history, the same way every time for the same host.
        /// </summary>
        /// <remarks>
        /// The outages go down first, then the network-wide ones over them, then the stretches nobody
        /// pinged the host, which win over everything. The scattered losses go last, onto answered
        /// rounds between answered neighbours, so each stays a single lost ping rather than lengthening
        /// an outage it happened to land beside. The last rounds are kept clear of them, so a host is
        /// not caught mid-blip at the end and every host but the two still down reads as up.
        /// </remarks>
        public static RoundState[] Plan(int hostIndex)
        {
            var host = Hosts[hostIndex];
            var random = new Random(1_000 + hostIndex);
            var states = new RoundState[RoundCount];

            foreach (var episode in host.Episodes)
            {
                switch (episode)
                {
                    case Outage outage:
                        Mark(states, RoundAt(outage.HoursBeforeEnd), outage.Pings ?? RoundCount, RoundState.Missed);
                        break;

                    case Daily daily:
                        for (var hours = daily.HoursBeforeEnd; hours <= Math.Min(daily.Since, HistoryHours); hours += 24)
                        {
                            if (hours >= daily.Until)
                            {
                                Mark(states, RoundAt(hours), random.Next(daily.MinPings, daily.MaxPings + 1), RoundState.Missed);
                            }
                        }

                        break;

                    case Flapping flapping:
                        var spacing = (flapping.FromHoursBeforeEnd - flapping.ToHoursBeforeEnd) / flapping.Runs;
                        for (var run = 0; run < flapping.Runs; run++)
                        {
                            var jitter = (random.NextDouble() - 0.5) * spacing / 2;
                            var hours = flapping.FromHoursBeforeEnd - (spacing * (run + 0.5)) + jitter;
                            Mark(states, RoundAt(hours), random.Next(flapping.MinPings, flapping.MaxPings + 1), RoundState.Missed);
                        }

                        break;
                }
            }

            foreach (var (hoursBeforeEnd, length) in NetworkOutages)
            {
                var start = RoundAt(hoursBeforeEnd);
                for (var round = start; round < Math.Min(start + Rounds(length), RoundCount); round++)
                {
                    states[round] = host.Kind switch
                    {
                        AddressKind.Name => RoundState.Unresolved,
                        AddressKind.IpAddress => RoundState.Missed,
                        _ => states[round],
                    };
                }
            }

            if (host.AddedHoursBeforeEnd is double added)
            {
                Mark(states, 0, RoundAt(added), RoundState.NotPinged);
            }

            foreach (var (hoursBeforeEnd, length) in ServiceStops)
            {
                Mark(states, RoundAt(hoursBeforeEnd), Rounds(length), RoundState.NotPinged);
            }

            foreach (var paused in host.Episodes.OfType<Paused>())
            {
                Mark(states, RoundAt(paused.HoursBeforeEnd), Rounds(paused.For), RoundState.NotPinged);
            }

            var firstRound = host.AddedHoursBeforeEnd is double addedAt ? RoundAt(addedAt) : 0;
            foreach (var scattered in host.Episodes.OfType<Scattered>())
            {
                var from = Math.Max(firstRound + 1, RoundAt(scattered.Since));
                var to = Math.Min(RoundCount - 60, RoundAt(scattered.Until));
                for (var lost = 0; lost < scattered.Runs; lost++)
                {
                    for (var attempt = 0; attempt < 1_000; attempt++)
                    {
                        var round = random.Next(from, to);
                        if (AllAnswered(states, round - 1, 3))
                        {
                            states[round] = RoundState.Missed;
                            break;
                        }
                    }
                }
            }

            return states;
        }

        /// <summary>
        /// How many missed pings the Hosts summary steps over to find this host's last downtime: it
        /// reads them newest first and stops at the first that ends a run as long as
        /// <see cref="RetryAttempts"/> makes a downtime, so it is one step for each lost ping since the
        /// last downtime and one for the downtime itself. See HostSummary.BuildQuery.
        /// </summary>
        public static int SummaryWalk(RoundState[] plan)
        {
            var steps = 0;
            var round = plan.Length - 1;
            while (true)
            {
                while (round >= 0 && plan[round] != RoundState.Missed)
                {
                    round--;
                }

                if (round < 0)
                {
                    return steps;
                }

                // The run this miss ends: back to the answered ping before it, across the rounds that
                // recorded nothing, which separate no run.
                steps++;
                var run = 0;
                while (round >= 0 && plan[round] != RoundState.Answered)
                {
                    run += plan[round] == RoundState.Missed ? 1 : 0;
                    round--;
                }

                if (run > RetryAttempts)
                {
                    return steps;
                }
            }
        }

        /// <summary>
        /// A hash of everything the template is written from — the plans, the hosts and their round
        /// trips, the settings and <see cref="Version"/> — which names the cached template, so that a
        /// change to any of them seeds a new one.
        /// </summary>
        public static string Fingerprint(IReadOnlyList<RoundState[]> plans)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"{Version}|{IntervalSeconds}|{RetryAttempts}|{TimeoutSeconds}|{ResolveTimeoutSeconds}|{LastRoundUtc.Ticks}|")));
            foreach (var host in Hosts)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                    $"{host.Name}|{host.Address}|{host.Kind}|{host.AddedHoursBeforeEnd}|{host.Roundtrip}|")));
            }

            foreach (var plan in plans)
            {
                hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(plan.AsSpan()));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
        }

        private static int Rounds(TimeSpan length) => (int)Math.Round(length / Interval);

        private static void Mark(RoundState[] states, int start, int rounds, RoundState state)
        {
            var end = (int)Math.Min((long)start + rounds, RoundCount);
            Array.Fill(states, state, start, end - start);
        }

        private static bool AllAnswered(RoundState[] states, int start, int rounds) =>
            Array.IndexOf(states, RoundState.Missed, start, rounds) < 0
            && Array.IndexOf(states, RoundState.Unresolved, start, rounds) < 0
            && Array.IndexOf(states, RoundState.NotPinged, start, rounds) < 0;
    }
}
