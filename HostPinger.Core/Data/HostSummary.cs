using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    /// <summary>A monitored host together with the most recent attempt recorded against it.</summary>
    /// <param name="Host">The host row.</param>
    /// <param name="Last">Its latest attempt, or null when the host has never been pinged.</param>
    /// <param name="LastDowntime">Its most recent stretch of unanswered pings, or null when every ping was answered.</param>
    public sealed record HostSummary(MonitoredHost Host, LastPing? Last, Downtime? LastDowntime)
    {
        /// <summary>
        /// Loads every host with its latest attempt and its last downtime, ordered by name.
        /// </summary>
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
            CancellationToken cancellationToken = default)
        {
            var rows = await BuildQuery(db).ToListAsync(cancellationToken);

            // A missing timestamp is the only reliable "never pinged" signal: RoundtripMs is null
            // both for a host with no attempts and for one whose last ping went unanswered.
            return rows
                .Select(r => new HostSummary(
                    r.Host,
                    r.LastTimestampUtc is { } timestamp ? new LastPing(timestamp, r.LastRoundtripMs) : null,
                    // The first missed ping is what says a downtime happened at all; the last
                    // answered one before it is where that downtime is measured from, and only a
                    // host that has never answered is missing one.
                    r.DowntimeFirstMissedUtc is { } firstMissed
                        ? new Downtime(r.DowntimeLastSeenUpUtc ?? firstMissed, r.DowntimeEndUtc)
                        : null))
                .ToList();
        }

        /// <summary>
        /// Reads the last downtime as the window bounded by the answered pings on either side of
        /// the host's most recent unanswered ping: from the last ping it answered before that to
        /// the ping it answered after.
        /// </summary>
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
        /// Every subquery is written as an indexed "ORDER BY TimestampUtc LIMIT 1" seek rather than
        /// a MIN/MAX aggregate, for the reason given on <see cref="LoadAsync"/>. Two of them lean on
        /// the fact that the run of unanswered pings is contiguous: the first attempt after the last
        /// answered ping must itself be unanswered, and the first attempt after the last unanswered
        /// ping must be answered, so neither has to filter on RoundtripMs and both seek
        /// IX_PingAttempts_HostId_TimestampUtc directly. Finding the answered ping that precedes the
        /// run is the one walk over unindexed rows, and it only covers the run itself.
        /// </para>
        /// </remarks>
        internal static IQueryable<HostSummaryRow> BuildQuery(HostPingerDbContext db) =>
            from h in db.Hosts.AsNoTracking()

                // The end of the last downtime — everything after it was answered.
            let lastFailureUtc = h.PingAttempts
                .Where(a => a.RoundtripMs == null)
                .OrderByDescending(a => a.TimestampUtc)
                .Select(a => (DateTime?)a.TimestampUtc)
                .FirstOrDefault()

            // The last ping answered before that failure, which is what the run started after.
            let lastSuccessUtc = h.PingAttempts
                .Where(a => a.RoundtripMs != null && a.TimestampUtc < lastFailureUtc)
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
                DowntimeLastSeenUpUtc = lastSuccessUtc,

                // The first ping of the run. A host that has never been answered has no
                // lastSuccessUtc to start after; the minimum date stands in for it so this stays a
                // range seek rather than an OR.
                DowntimeFirstMissedUtc = h.PingAttempts
                    .Where(a => a.RoundtripMs == null && a.TimestampUtc > (lastSuccessUtc ?? DateTime.MinValue))
                    .OrderBy(a => a.TimestampUtc)
                    .Select(a => (DateTime?)a.TimestampUtc)
                    .FirstOrDefault(),

                // Null while the host is still down, and null too when it was never down at all —
                // DowntimeFirstMissedUtc tells those apart.
                DowntimeEndUtc = h.PingAttempts
                    .Where(a => a.TimestampUtc > lastFailureUtc)
                    .OrderBy(a => a.TimestampUtc)
                    .Select(a => (DateTime?)a.TimestampUtc)
                    .FirstOrDefault(),
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
        }
    }

    /// <summary>The outcome of the most recent ping of a host.</summary>
    /// <param name="TimestampUtc">When the attempt was made.</param>
    /// <param name="RoundtripMs">The round trip in milliseconds, or null when the host did not reply.</param>
    public sealed record LastPing(DateTime TimestampUtc, int? RoundtripMs);

    /// <summary>
    /// A stretch over which a host was never seen reachable, bounded by the answered pings around
    /// the run of unanswered ones. It is deliberately the widest window the recorded attempts
    /// support rather than the narrowest: nothing was recorded between these two pings that shows
    /// the host answering, including for however long the monitor itself was not running.
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
