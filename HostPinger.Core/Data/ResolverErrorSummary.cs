using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    /// <summary>
    /// One address that has failed to resolve, with when it last did and how often it has lately.
    /// </summary>
    /// <param name="Address">The address the resolver was asked about.</param>
    /// <param name="HostName">
    /// The name of the host currently configured with that address, or null when no host carries it
    /// any more — it was re-pointed or deleted after these errors were recorded.
    /// </param>
    /// <param name="LastUtc">When it last failed to resolve.</param>
    /// <param name="LastReason">Which way that most recent lookup failed.</param>
    /// <param name="Last24Hours">How many failures were recorded for it in the last 24 hours.</param>
    /// <param name="Last7Days">How many were recorded in the last 7 days.</param>
    /// <param name="Last30Days">
    /// How many were recorded in the last 30 days, which is everything kept: see
    /// <see cref="ResolverError.Retention"/>.
    /// </param>
    public sealed record ResolverErrorSummary(
        string Address,
        string? HostName,
        DateTime LastUtc,
        ResolverFailure LastReason,
        int Last24Hours,
        int Last7Days,
        int Last30Days)
    {
        /// <summary>
        /// Loads one row per address that has ever failed to resolve, most recent failure first.
        /// </summary>
        /// <param name="db">The context to read through.</param>
        /// <param name="nowUtc">The moment the two count windows are measured back from.</param>
        /// <param name="cancellationToken">Cancels the queries.</param>
        /// <remarks>
        /// Every recorded error is grouped rather than only those inside a window, so an address
        /// that stopped failing a fortnight ago is still listed — with two zero counts against a
        /// thirty-day one, which is precisely the reading that it was a problem and is not one now.
        /// What ends a listing is the pruner rather than this query: once the last failure has aged
        /// past <see cref="ResolverError.Retention"/> there are no rows left to group. The grouping
        /// runs off IX_ResolverErrors_Address_TimestampUtc; see the index for why it can afford to
        /// take in everything.
        /// </remarks>
        public static async Task<List<ResolverErrorSummary>> LoadAsync(
            HostPingerDbContext db,
            DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            var since24Hours = nowUtc - TimeSpan.FromHours(24);
            var since7Days = nowUtc - TimeSpan.FromDays(7);
            var since30Days = nowUtc - ResolverError.Retention;

            // Rows are only ever appended, one round after another, so the largest id in a group is
            // that address's most recent failure. Carrying the id out of the grouping is what lets
            // its reason be fetched below without the grouping itself having to read any rows.
            var grouped = await db.ResolverErrors.AsNoTracking()
                .GroupBy(e => e.Address)
                .Select(g => new
                {
                    Address = g.Key,
                    LastId = g.Max(e => e.Id),
                    LastUtc = g.Max(e => e.TimestampUtc),
                    Last24Hours = g.Count(e => e.TimestampUtc >= since24Hours),
                    Last7Days = g.Count(e => e.TimestampUtc >= since7Days),

                    // Counted rather than taken as the size of the group: the round that prunes is
                    // the round that records, so a row can sit a moment past the retention before
                    // the pass that removes it, and the column would read one higher than the
                    // window it names.
                    Last30Days = g.Count(e => e.TimestampUtc >= since30Days),
                })
                .ToListAsync(cancellationToken);

            if (grouped.Count == 0)
            {
                return [];
            }

            // One row per address from here on, so both lookups are as small as the page is long.
            var lastIds = grouped.Select(row => row.LastId).ToList();
            var reasons = await db.ResolverErrors.AsNoTracking()
                .Where(e => lastIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Reason, cancellationToken);

            var addresses = grouped.Select(row => row.Address).ToList();
            var hostNames = await db.Hosts.AsNoTracking()
                .Where(h => addresses.Contains(h.Address))
                .ToDictionaryAsync(h => h.Address, h => h.Name, cancellationToken);

            return grouped
                // Ties are the addresses that failed in the same round, which is the ordinary case
                // when a resolver is unreachable; by address keeps their order steady across a
                // refresh rather than leaving it to the database.
                .OrderByDescending(row => row.LastUtc)
                .ThenBy(row => row.Address, StringComparer.OrdinalIgnoreCase)
                .Select(row => new ResolverErrorSummary(
                    row.Address,
                    hostNames.GetValueOrDefault(row.Address),
                    row.LastUtc,
                    reasons[row.LastId],
                    row.Last24Hours,
                    row.Last7Days,
                    row.Last30Days))
                .ToList();
        }
    }
}
