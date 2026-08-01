using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    /// <summary>A monitored host together with the most recent attempt recorded against it.</summary>
    /// <param name="Host">The host row.</param>
    /// <param name="Last">Its latest attempt, or null when the host has never been pinged.</param>
    public sealed record HostSummary(MonitoredHost Host, LastPing? Last)
    {
        /// <summary>
        /// Loads every host with its latest attempt, ordered by name.
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
                    r.LastTimestampUtc is { } timestamp ? new LastPing(timestamp, r.LastRoundtripMs) : null))
                .ToList();
        }

        internal static IQueryable<HostSummaryRow> BuildQuery(HostPingerDbContext db) =>
            db.Hosts.AsNoTracking()
                .OrderBy(h => h.Name)
                .Select(h => new HostSummaryRow
                {
                    Host = h,
                    LastTimestampUtc = h.PingAttempts.OrderByDescending(a => a.TimestampUtc)
                        .Select(a => (DateTime?)a.TimestampUtc).FirstOrDefault(),
                    LastRoundtripMs = h.PingAttempts.OrderByDescending(a => a.TimestampUtc)
                        .Select(a => a.RoundtripMs).FirstOrDefault(),
                });

        /// <summary>The flat shape the database returns, before the null timestamp is folded away.</summary>
        internal sealed class HostSummaryRow
        {
            public required MonitoredHost Host { get; init; }

            public required DateTime? LastTimestampUtc { get; init; }

            public required int? LastRoundtripMs { get; init; }
        }
    }

    /// <summary>The outcome of the most recent ping of a host.</summary>
    /// <param name="TimestampUtc">When the attempt was made.</param>
    /// <param name="RoundtripMs">The round trip in milliseconds, or null when the host did not reply.</param>
    public sealed record LastPing(DateTime TimestampUtc, int? RoundtripMs);
}
