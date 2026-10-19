using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// The reads the pages make of the database on demand, each through a context of its own: the
    /// graph, the resolver errors, and the figures under the Configuration page's settings. The
    /// Hosts page's summary is not among them; <see cref="HostSummaryCache"/> keeps that one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What each read finds is worked out by the type that owns its query — <see cref="GraphRead"/>,
    /// <see cref="ResolverErrorSummary"/>, <see cref="DatabaseStats"/> — and is tested there. This
    /// is where the context they read through comes from and goes back to, so that no page holds
    /// one.
    /// </para>
    /// <para>
    /// A read runs on the thread that calls it, for as long as its statements take:
    /// Microsoft.Data.Sqlite does no asynchronous I/O, so awaiting one holds the caller's thread
    /// throughout. A component therefore never awaits one on its circuit, where every click on the
    /// page would wait behind it, but sends it through PageReads, which decides where each read
    /// runs. That decision cannot be made here: the one read PageReads keeps on the circuit, a
    /// page's first, is kept there so that the page does not draw its empty state over the
    /// prerendered one, and a read that sent itself to the thread pool would do exactly that.
    /// </para>
    /// </remarks>
    public sealed class DatabaseReads(IDbContextFactory<HostPingerDbContext> dbFactory)
    {
        /// <summary>The graph: see <see cref="GraphRead.LoadAsync"/>.</summary>
        public async Task<GraphRead> ReadGraphAsync(
            IReadOnlySet<int> plotted,
            IReadOnlyDictionary<int, ChartSeriesRead>? earlier,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int points,
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await GraphRead.LoadAsync(db, plotted, earlier, rangeStartUtc, rangeEndUtc, points, cancellationToken);
        }

        /// <summary>The addresses that have failed to resolve: see <see cref="ResolverErrorSummary.LoadAsync"/>.</summary>
        public async Task<List<ResolverErrorSummary>> ReadResolverErrorsAsync(
            DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await ResolverErrorSummary.LoadAsync(db, nowUtc, cancellationToken);
        }

        /// <summary>The database's size and growth: see <see cref="DatabaseStats.CollectAsync"/>.</summary>
        public async Task<DatabaseStats> ReadStatsAsync(
            PingerOptions options,
            CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await DatabaseStats.CollectAsync(db, options, cancellationToken);
        }
    }
}
