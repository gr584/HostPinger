using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    /// <summary>
    /// What one read of the graph brings back: every host; the read behind the series of each
    /// plotted one, which is also what the next refresh carries on from; and the range and density
    /// they were read over.
    /// </summary>
    /// <param name="Hosts">Every host, in the order they were added; see <see cref="LoadAsync"/>.</param>
    /// <param name="SeriesReads">The series read for each plotted host that exists, by host id.</param>
    /// <param name="RangeStartUtc">The oldest instant the series cover.</param>
    /// <param name="RangeEndUtc">The newest.</param>
    /// <param name="Points">How many points each series was read at.</param>
    /// <remarks>
    /// Never changed once read, and shared: the prerender holds the one it drew for the live page
    /// that follows, and a refresh carries on from the one on screen while the page moves on.
    /// </remarks>
    public sealed record GraphRead(
        IReadOnlyList<MonitoredHost> Hosts,
        IReadOnlyDictionary<int, ChartSeriesRead> SeriesReads,
        DateTime RangeStartUtc,
        DateTime RangeEndUtc,
        int Points)
    {
        /// <summary>
        /// Reads every host, and a series over the range for each plotted host that still exists:
        /// carried on from <paramref name="earlier"/> for a host it holds a read of, and read from
        /// scratch for any other.
        /// </summary>
        /// <param name="db">The context to read through.</param>
        /// <param name="plotted">The hosts to read a series for. One that does not exist is left out.</param>
        /// <param name="earlier">
        /// The reads behind the series on screen, for a refresh to carry on from, or null to read
        /// every series from scratch. Whether a read can be carried on is decided per host by
        /// <see cref="ChartSeries.RefreshAsync"/>.
        /// </param>
        /// <param name="rangeStartUtc">The oldest instant on the chart.</param>
        /// <param name="rangeEndUtc">The newest.</param>
        /// <param name="points">How many points the chart has room for.</param>
        /// <param name="cancellationToken">Cancels the read between statements.</param>
        /// <remarks>
        /// <para>
        /// The hosts come in the order they were added, which is what each one's colour is taken
        /// from: a list in name order would repaint every host after one added or renamed.
        /// </para>
        /// <para>
        /// A read per plotted host, each already reduced to the points the chart has room for. One
        /// query covering every host would have to sort what it finds back into time order, where a
        /// single host is a range seek straight down IX_PingAttempts_HostId_TimestampUtc; and
        /// reading the attempts themselves to average them in memory is what made opening the graph
        /// on a long outage cost thirteen seconds. See <see cref="ChartSeries"/>, which is where
        /// both go.
        /// </para>
        /// </remarks>
        public static async Task<GraphRead> LoadAsync(
            HostPingerDbContext db,
            IReadOnlySet<int> plotted,
            IReadOnlyDictionary<int, ChartSeriesRead>? earlier,
            DateTime rangeStartUtc,
            DateTime rangeEndUtc,
            int points,
            CancellationToken cancellationToken = default)
        {
            var hosts = await db.Hosts.AsNoTracking().OrderBy(h => h.Id).ToListAsync(cancellationToken);
            var seriesReads = new Dictionary<int, ChartSeriesRead>();
            foreach (var host in hosts)
            {
                if (plotted.Contains(host.Id))
                {
                    seriesReads[host.Id] = earlier is not null && earlier.TryGetValue(host.Id, out var held)
                        ? await ChartSeries.RefreshAsync(db, host.Id, held, rangeStartUtc, rangeEndUtc, points, cancellationToken)
                        : await ChartSeries.LoadAsync(db, host.Id, rangeStartUtc, rangeEndUtc, points, cancellationToken);
                }
            }

            return new GraphRead(hosts, seriesReads, rangeStartUtc, rangeEndUtc, points);
        }
    }
}
