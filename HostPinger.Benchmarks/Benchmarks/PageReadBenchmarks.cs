using BenchmarkDotNet.Attributes;
using HostPinger.Core.Data;
using HostPinger.Core.Services;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>
    /// The figures under the Configuration page's settings, <see cref="DatabaseReads.ReadStatsAsync"/>:
    /// the file's size and the attempts in it. Cold is the case that matters, since a count of the
    /// attempts used to scan an index for most of a minute on a database not in memory, holding up a
    /// ping round behind it; reading them off the ids made it two seeks.
    /// </summary>
    [BenchmarkCategory("Configuration")]
    [PageBenchmark(
        "Configuration page",
        "`DatabaseReads.ReadStatsAsync`: the database's size, attempt count and projected growth.")]
    public class ConfigurationPageBenchmarks : CacheStateBenchmark
    {
        [Benchmark]
        public Task<DatabaseStats> ReadStats() => Database.Reads.ReadStatsAsync(Database.Settings.CurrentPinger);
    }

    /// <summary>
    /// The Resolver errors page, <see cref="DatabaseReads.ReadResolverErrorsAsync"/>: every address
    /// that failed to resolve in the last thirty days, with its counts. Production's database had none
    /// left by the time it was copied; the seeded errors are the ones this build records for the gaps
    /// the network-wide outages leave in the hosts addressed by name. See <see cref="Seeding.SyntheticHistory"/>.
    /// </summary>
    [BenchmarkCategory("ResolverErrors")]
    [PageBenchmark(
        "Resolver errors page",
        "`DatabaseReads.ReadResolverErrorsAsync`: each address that failed to resolve in the last thirty days, with its counts.")]
    public class ResolverErrorsPageBenchmarks : CacheStateBenchmark
    {
        [Benchmark]
        public Task<List<ResolverErrorSummary>> ReadResolverErrors() => Database.Reads.ReadResolverErrorsAsync(Database.NowUtc);
    }
}
