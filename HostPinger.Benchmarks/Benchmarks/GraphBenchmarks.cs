using BenchmarkDotNet.Attributes;
using HostPinger.Benchmarks.Infrastructure;
using HostPinger.Benchmarks.Seeding;
using HostPinger.Core.Charting;
using HostPinger.Core.Data;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>What the graph is showing.</summary>
    public enum GraphRange
    {
        /// <summary>The page's default: the last hour, few enough attempts to draw as recorded.</summary>
        LastHour,

        Last24Hours,

        /// <summary>The widest of the page's preset windows.</summary>
        Last7Days,

        /// <summary>
        /// What the Hosts page links to from the longest outage's downtime: the outage with half its
        /// length again before it, which for one forty-one days old is sixty-one days of a single host,
        /// its whole history.
        /// </summary>
        AroundOngoingOutage,
    }

    /// <summary>
    /// Opening the graph: <see cref="Core.Services.DatabaseReads.ReadGraphAsync"/> reading every series
    /// from scratch, as a new page or a changed window does.
    /// </summary>
    [BenchmarkCategory("Graph")]
    [PageBenchmark(
        "Graph page, opening",
        "`DatabaseReads.ReadGraphAsync` reading every series from scratch at 600 points: eight hosts over a preset window, or the longest outage's own graph.")]
    public class GraphOpenBenchmarks : CacheStateBenchmark
    {
        [ParamsAllValues]
        public GraphRange Range { get; set; }

        [Benchmark]
        public Task<GraphRead> Open() => GraphView.Opening(Database, Range).ReadAsync(Database, onScreen: null);
    }

    /// <summary>
    /// The graph's five-second refresh: <see cref="Core.Services.DatabaseReads.ReadGraphAsync"/>
    /// carrying on from the read on screen, so that only the buckets still filling are read again.
    /// </summary>
    /// <remarks>
    /// Every refresh here follows a round, which in the application is every other one. The page also
    /// reads from scratch every sixtieth refresh, once every five minutes, which is what
    /// <see cref="GraphOpenBenchmarks"/> measures, warm.
    /// </remarks>
    [BenchmarkCategory("Graph")]
    [PageBenchmark(
        "Graph page, refreshing",
        "`DatabaseReads.ReadGraphAsync` carrying on from the read on screen after a round, as the page does every five seconds.")]
    public class GraphRefreshBenchmarks : DatabaseBenchmark
    {
        private GraphView _view = null!;
        private GraphRead _onScreen = null!;

        [ParamsAllValues]
        public GraphRange Range { get; set; }

        [Benchmark]
        public async Task<GraphRead> Refresh() => _onScreen = await _view.ReadAsync(Database, _onScreen);

        private protected override async Task OnOpenedAsync()
        {
            _view = GraphView.Opening(Database, Range);
            _onScreen = await _view.ReadAsync(Database, onScreen: null);
        }
    }

    /// <summary>
    /// A graph as the page holds it: the hosts plotted and the width of the window ending at now,
    /// read at the page's default density.
    /// </summary>
    internal sealed record GraphView(IReadOnlySet<int> Plotted, TimeSpan Width)
    {
        /// <summary>PingGraph's DefaultPointsPerSeries, the density it reads at until the chart has measured itself.</summary>
        public const int Points = 600;

        public static GraphView Opening(WorkingDatabase database, GraphRange range) => range switch
        {
            GraphRange.LastHour => new(database.ChartedHosts, TimeSpan.FromHours(1)),
            GraphRange.Last24Hours => new(database.ChartedHosts, TimeSpan.FromHours(24)),
            GraphRange.Last7Days => new(database.ChartedHosts, TimeSpan.FromDays(7)),
            GraphRange.AroundOngoingOutage => AroundOngoingOutage(database),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
        };

        public Task<GraphRead> ReadAsync(WorkingDatabase database, GraphRead? onScreen)
        {
            var nowUtc = database.NowUtc;
            return database.Reads.ReadGraphAsync(Plotted, onScreen?.SeriesReads, nowUtc - Width, nowUtc, Points);
        }

        /// <summary>The opening the Hosts page builds for the link on a downtime, the way it builds it.</summary>
        private static GraphView AroundOngoingOutage(WorkingDatabase database)
        {
            var host = database.Host(SyntheticHistory.LongestOutage);
            var downtime = host.LastDowntime
                ?? throw new InvalidOperationException($"{SyntheticHistory.LongestOutage} has no downtime to open the graph on.");
            var nowUtc = database.NowUtc;
            return GraphOpening.Around(downtime.StartedUtc, downtime.EndedUtc ?? nowUtc, nowUtc) is GraphOpening.Window window
                ? new GraphView(new HashSet<int> { host.Host.Id }, window.Width)
                : throw new InvalidOperationException("An outage still running should open as a window ending at now.");
        }
    }
}
