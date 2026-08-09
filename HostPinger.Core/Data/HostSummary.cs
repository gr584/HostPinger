using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    /// <summary>A monitored host together with the most recent attempt recorded against it.</summary>
    /// <param name="Host">The host row.</param>
    /// <param name="Last">Its latest attempt, or null when the host has never been pinged.</param>
    /// <param name="LastDowntime">
    /// Its most recent stretch of unanswered pings long enough to count as one, or null when it
    /// has never had one.
    /// </param>
    /// <param name="MissedPings">
    /// How many pings it has missed in a row as of now — zero whenever the last ping was answered.
    /// Counted no further than the threshold that makes it down, which is all
    /// <see cref="Status"/> needs to know.
    /// </param>
    /// <param name="Status">Where those leave the host: paused, waiting, up, retrying or down.</param>
    public sealed record HostSummary(
        MonitoredHost Host,
        LastPing? Last,
        Downtime? LastDowntime,
        int MissedPings,
        HostStatus Status)
    {
        /// <summary>
        /// Loads every host with its latest attempt, its last downtime and its status, ordered by
        /// name.
        /// </summary>
        /// <param name="db">The context to read through.</param>
        /// <param name="retryAttempts">
        /// How many times a host that misses a ping is retried before it counts as down, from
        /// <c>PingerOptions.RetryAttempts</c>. Zero makes the first missed ping count. Negative
        /// values are read as zero.
        /// </param>
        /// <param name="cancellationToken">Cancels the query.</param>
        /// <remarks>
        /// The last attempt is pulled one scalar at a time on purpose. Projecting the attempt as an
        /// entity — <c>h.PingAttempts.OrderByDescending(a =&gt; a.TimestampUtc).FirstOrDefault()</c> —
        /// makes EF emit a ROW_NUMBER() window partitioned over the whole PingAttempts table, so the
        /// query scans every attempt ever recorded and slows down without bound as history builds up.
        /// A scalar per column instead becomes a correlated "ORDER BY TimestampUtc DESC LIMIT 1"
        /// that seeks IX_PingAttempts_HostId_TimestampUtc and stays flat.
        /// <see cref="BuildQuery"/> is kept separate so tests can inspect the generated SQL.
        /// </remarks>
        public static async Task<List<HostSummary>> LoadAsync(
            HostPingerDbContext db,
            int retryAttempts,
            CancellationToken cancellationToken = default)
        {
            // The setting counts retries; everything below counts missed pings, which is one more
            // — the ping that failed in the first place, plus the retries it is allowed.
            var missesToDown = Math.Max(0, retryAttempts) + 1;
            var rows = await BuildQuery(db, missesToDown).ToListAsync(cancellationToken);

            // A missing timestamp is the only reliable "never pinged" signal: RoundtripMs is null
            // both for a host with no attempts and for one whose last ping went unanswered.
            return rows
                .Select(r =>
                {
                    var last = r.LastTimestampUtc is { } timestamp
                        ? new LastPing(timestamp, r.LastRoundtripMs)
                        : null;

                    return new HostSummary(
                        r.Host,
                        last,
                        // The first missed ping is what says a downtime happened at all; the last
                        // answered one before it is where that downtime is measured from, and only
                        // a host that has never answered is missing one.
                        r.DowntimeFirstMissedUtc is { } firstMissed
                            ? new Downtime(r.DowntimeLastSeenUpUtc ?? firstMissed, r.DowntimeEndUtc)
                            : null,
                        r.MissedPings,
                        StatusOf(r.Host, last, r.MissedPings, missesToDown));
                })
                .ToList();
        }

        /// <summary>
        /// What the host's state is, given how many pings it has missed in a row and how many make
        /// it down. A host that is not being pinged is neither up nor down, and one no round has
        /// covered yet has said nothing either way.
        /// </summary>
        private static HostStatus StatusOf(MonitoredHost host, LastPing? last, int missedPings, int missesToDown) =>
            (host.IsEnabled, last) switch
            {
                (false, _) => HostStatus.Paused,
                (true, null) => HostStatus.Waiting,
                (true, { RoundtripMs: not null }) => HostStatus.Up,
                _ when missedPings >= missesToDown => HostStatus.Down,
                _ => HostStatus.Retrying,
            };

        /// <summary>
        /// Reads the last downtime as the window bounded by the answered pings on either side of
        /// the host's most recent run of unanswered pings that reached
        /// <paramref name="missesToDown"/>: from the last ping it answered before that run to the
        /// ping it answered after.
        /// </summary>
        /// <param name="db">The context to read through.</param>
        /// <param name="missesToDown">
        /// How many pings missed in a row make a host down — one more than the retries
        /// <c>PingerOptions.RetryAttempts</c> allows it.
        /// </param>
        /// <remarks>
        /// <para>
        /// Both ends are answered pings on purpose, because those are the only moments the host is
        /// known to have been reachable. Measuring from the first <em>unanswered</em> ping instead
        /// reads a stretch with no attempts at all as uptime, which silently discards however long
        /// the monitor was not running: an outage that began while the service was stopped would be
        /// reported as starting when it came back. The cost is that a downtime is over-reported by
        /// up to one ping interval at each end, and that a monitor stopped for a long time makes
        /// the next unanswered ping look like a long outage.
        /// </para>
        /// <para>
        /// A run shorter than the threshold is not a downtime and is passed over, which is what
        /// keeps this column saying the same thing as the status: a host that is still retrying
        /// goes on showing whatever outage it last had, rather than the blip it is in the middle
        /// of. The retries are still inside the downtime once one is reported, because they are
        /// time the host was not answering — the threshold decides whether an outage is reported,
        /// not when it started.
        /// </para>
        /// <para>
        /// Every subquery is written as an indexed "ORDER BY TimestampUtc LIMIT 1" seek rather than
        /// a MIN/MAX aggregate, for the reason given on <see cref="LoadAsync"/>. Two of them lean on
        /// the fact that a run of unanswered pings is contiguous: the first attempt after the last
        /// answered ping must itself be unanswered, and the first attempt after the last unanswered
        /// ping of a run must be answered, so neither has to filter on RoundtripMs and both seek
        /// IX_PingAttempts_HostId_TimestampUtc directly. Finding the answered ping that precedes a
        /// run is the one walk over unindexed rows, and it only covers the run itself.
        /// </para>
        /// </remarks>
        internal static IQueryable<HostSummaryRow> BuildQuery(HostPingerDbContext db, int missesToDown) =>
            from h in db.Hosts.AsNoTracking()

                // The last ping the host answered — the last moment it is known to have been
                // reachable. Every attempt recorded after it went unanswered, which is what makes
                // it the start of the run the host is in now.
            let lastAnsweredUtc = h.PingAttempts
                .Where(a => a.RoundtripMs != null)
                .OrderByDescending(a => a.TimestampUtc)
                .Select(a => (DateTime?)a.TimestampUtc)
                .FirstOrDefault()

            // The last missed ping of the last downtime: the most recent one whose run had reached
            // the threshold by the time it was recorded. It is that run's final missed ping, because
            // a later one in the same run would satisfy the same test and be more recent still.
            //
            // The count is of every attempt since the answered ping before x, which is the run
            // holding x — the pings in between are unanswered by definition of that answered ping
            // being the last one. Counting stops at the threshold, since reaching it is the whole
            // question.
            //
            // Reading the candidates newest-first off IX_PingAttempts_Unanswered stops the search at
            // the first run that qualifies: none at all for a host that has always answered, one
            // step for a host that is down now, and one step per missed ping since the last real
            // outage for a host that drops the odd packet.
            let downtimeLastFailureUtc = h.PingAttempts
                .Where(x => x.RoundtripMs == null
                    && h.PingAttempts
                        .Where(a => a.TimestampUtc <= x.TimestampUtc && a.TimestampUtc > (h.PingAttempts
                            .Where(s => s.RoundtripMs != null && s.TimestampUtc < x.TimestampUtc)
                            .OrderByDescending(s => s.TimestampUtc)
                            .Select(s => (DateTime?)s.TimestampUtc)
                            .FirstOrDefault() ?? DateTime.MinValue))
                        .Take(missesToDown)
                        .Count() == missesToDown)
                .OrderByDescending(x => x.TimestampUtc)
                .Select(x => (DateTime?)x.TimestampUtc)
                .FirstOrDefault()

            // The last ping answered before that run, which is what it started after.
            let downtimeLastSeenUpUtc = h.PingAttempts
                .Where(a => a.RoundtripMs != null && a.TimestampUtc < downtimeLastFailureUtc)
                .OrderByDescending(a => a.TimestampUtc)
                .Select(a => (DateTime?)a.TimestampUtc)
                .FirstOrDefault()

            orderby h.Name
            select new HostSummaryRow
            {
                Host = h,
                LastTimestampUtc = h.PingAttempts.OrderByDescending(a => a.TimestampUtc)
                    .Select(a => (DateTime?)a.TimestampUtc).FirstOrDefault(),
                LastRoundtripMs = h.PingAttempts.OrderByDescending(a => a.TimestampUtc)
                    .Select(a => a.RoundtripMs).FirstOrDefault(),

                // Where the downtime is measured from. Null only for a host that has never
                // answered a ping, which has nothing to have gone down from.
                DowntimeLastSeenUpUtc = downtimeLastSeenUpUtc,

                // The first ping of that run. A host that has never answered has no
                // downtimeLastSeenUpUtc to start after; the minimum date stands in for it so this
                // stays a range seek rather than an OR. The upper bound is what makes this null
                // when no run has reached the threshold: without it, the missed pings of a run
                // still short of one would be reported as a downtime that has not happened.
                DowntimeFirstMissedUtc = h.PingAttempts
                    .Where(a => a.RoundtripMs == null
                        && a.TimestampUtc > (downtimeLastSeenUpUtc ?? DateTime.MinValue)
                        && a.TimestampUtc <= downtimeLastFailureUtc)
                    .OrderBy(a => a.TimestampUtc)
                    .Select(a => (DateTime?)a.TimestampUtc)
                    .FirstOrDefault(),

                // Null while the host is still down, and null too when it was never down at all —
                // DowntimeFirstMissedUtc tells those apart.
                DowntimeEndUtc = h.PingAttempts
                    .Where(a => a.TimestampUtc > downtimeLastFailureUtc)
                    .OrderBy(a => a.TimestampUtc)
                    .Select(a => (DateTime?)a.TimestampUtc)
                    .FirstOrDefault(),

                // How long the host has been missing pings for now, which is what decides whether
                // it is retrying or down. Everything after the last answered ping is that run and
                // nothing else is, so a host that is answering counts nothing. Counting stops at
                // the threshold for the same reason as above.
                MissedPings = h.PingAttempts
                    .Where(a => a.RoundtripMs == null && a.TimestampUtc > (lastAnsweredUtc ?? DateTime.MinValue))
                    .Take(missesToDown)
                    .Count(),
            };

        /// <summary>The flat shape the database returns, before the null timestamps are folded away.</summary>
        internal sealed class HostSummaryRow
        {
            public required MonitoredHost Host { get; init; }

            public required DateTime? LastTimestampUtc { get; init; }

            public required int? LastRoundtripMs { get; init; }

            public required DateTime? DowntimeLastSeenUpUtc { get; init; }

            public required DateTime? DowntimeFirstMissedUtc { get; init; }

            public required DateTime? DowntimeEndUtc { get; init; }

            public required int MissedPings { get; init; }
        }
    }

    /// <summary>
    /// What a host's recorded attempts say about it right now, in the order a host that stops
    /// answering moves through.
    /// </summary>
    public enum HostStatus
    {
        /// <summary>Not being pinged at all, so nothing is being claimed about it.</summary>
        Paused,

        /// <summary>Enabled, but no round has covered it yet.</summary>
        Waiting,

        /// <summary>The last ping was answered.</summary>
        Up,

        /// <summary>
        /// Pings are going unanswered, but fewer in a row than it takes to count as down. A host
        /// sits here while a dropped packet or a moment of congestion is still a possible
        /// explanation.
        /// </summary>
        Retrying,

        /// <summary>Unanswered for as many pings in a row as the configuration allows.</summary>
        Down,
    }

    /// <summary>The outcome of the most recent ping of a host.</summary>
    /// <param name="TimestampUtc">When the attempt was made.</param>
    /// <param name="RoundtripMs">The round trip in milliseconds, or null when the host did not reply.</param>
    public sealed record LastPing(DateTime TimestampUtc, int? RoundtripMs);

    /// <summary>
    /// A stretch over which a host was never seen reachable, bounded by the answered pings around
    /// a run of unanswered ones long enough to count as a downtime. It is deliberately the widest
    /// window the recorded attempts support rather than the narrowest: nothing was recorded between
    /// these two pings that shows the host answering, including for however long the monitor itself
    /// was not running.
    /// </summary>
    /// <param name="StartedUtc">
    /// The last ping the host answered before the outage — the last moment it was known reachable.
    /// The outage began at some point after it, up to one ping interval later. For a host that has
    /// never answered a ping this is its first recorded attempt instead.
    /// </param>
    /// <param name="EndedUtc">The ping that was answered again, or null while the host is still down.</param>
    public sealed record Downtime(DateTime StartedUtc, DateTime? EndedUtc)
    {
        /// <summary>True while no ping has been answered since <see cref="StartedUtc"/>.</summary>
        public bool IsOngoing => EndedUtc is null;

        /// <summary>
        /// How long the host went unanswered: from the last ping it answered to the one that
        /// answered again, or to nowUtc while the downtime is still ongoing. An ongoing downtime
        /// therefore keeps growing while the monitor is stopped, which is the point — a host that
        /// is not being pinged is not a host that is known to be up.
        /// </summary>
        public TimeSpan DurationAt(DateTime nowUtc) => (EndedUtc ?? nowUtc) - StartedUtc;

        /// <summary>Renders a duration as its two largest non-zero units, e.g. "45s", "3m 20s", "2h 5m", "4d 6h".</summary>
        public static string FormatDuration(TimeSpan duration)
        {
            var whole = duration > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Floor(duration.TotalSeconds))
                : TimeSpan.Zero;

            return whole switch
            {
                { TotalMinutes: < 1 } => $"{whole.Seconds}s",
                { TotalHours: < 1 } => Pair(whole.Minutes, "m", whole.Seconds, "s"),
                { TotalDays: < 1 } => Pair(whole.Hours, "h", whole.Minutes, "m"),
                _ => Pair((int)whole.TotalDays, "d", whole.Hours, "h"),
            };

            static string Pair(int major, string majorUnit, int minor, string minorUnit) =>
                minor == 0 ? $"{major}{majorUnit}" : $"{major}{majorUnit} {minor}{minorUnit}";
        }
    }
}
