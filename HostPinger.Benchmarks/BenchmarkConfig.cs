using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using Perfolizer.Horology;

namespace HostPinger.Benchmarks
{
    /// <summary>How every benchmark here is run and reported.</summary>
    internal static class BenchmarkConfig
    {
        /// <summary>
        /// One call per iteration, timed on its own, after two to warm up: BenchmarkDotNet's monitoring
        /// strategy, meant for operations long enough that the timer's overhead is nothing beside them,
        /// and the only sensible one for calls that need a round landed or the cache dropped before each.
        /// Fifteen iterations keep the whole run near a quarter of an hour on a CI runner. A class can
        /// ask for fewer, as the delete with its history does.
        /// </summary>
        /// <remarks>
        /// The median, minimum and maximum beside the mean, because a disk is not a steady thing to time
        /// against and a single slow read would otherwise only show up as a wider error.
        /// </remarks>
        public static IConfig Create() =>
            DefaultConfig.Instance
                .AddJob(Job.Default
                    .WithStrategy(RunStrategy.Monitoring)
                    .WithLaunchCount(1)
                    .WithWarmupCount(2)
                    .WithIterationCount(15)
                    .AsDefault())
                .AddColumn(StatisticColumn.Median, StatisticColumn.Min, StatisticColumn.Max)
                .AddDiagnoser(MemoryDiagnoser.Default)
                .AddExporter(JsonExporter.Full)
                .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared))
                .WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond));
    }
}
