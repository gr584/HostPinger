using BenchmarkDotNet.Attributes;
using HostPinger.Core.Services;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>
    /// The Hosts page's summary, read by <see cref="HostSummaryCache"/> after every round: each host's
    /// last ping, last downtime and status. This is the query the month-long outages made slow — 2.45
    /// seconds on production's database before the answered-ping index, and the page froze whenever a
    /// read outlasted its refresh.
    /// </summary>
    /// <remarks>
    /// Read on the summary's own long-lived connection, as its loop reads it, straight after a round has
    /// landed: the commit empties that connection's SQLite cache, so the read goes to the operating
    /// system's for every page it touches. Warm only, because the loop reads the same pages every ten
    /// seconds and so keeps them in memory; a cold summary is the one read at startup, which
    /// <see cref="StartupBenchmarks"/> measures.
    /// </remarks>
    [BenchmarkCategory("Hosts")]
    [PageBenchmark(
        "Hosts page",
        "The summary `HostSummaryCache` reads after every ping round: last ping, last downtime and status of every host.")]
    public class HostsPageBenchmarks : DatabaseBenchmark
    {
        [Benchmark]
        public async Task<HostSummarySnapshot> SummaryAfterRound()
        {
            await Database.Summaries.LoadAsync();
            return Database.Summaries.Current;
        }
    }
}
