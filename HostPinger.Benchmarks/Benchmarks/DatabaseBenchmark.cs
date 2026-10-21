using BenchmarkDotNet.Attributes;
using HostPinger.Benchmarks.Infrastructure;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>Whether a read finds the database in the operating system's page cache.</summary>
    public enum CacheState
    {
        /// <summary>
        /// Whatever the warmup iterations and the reads before it left there: the case of a page used
        /// in a burst.
        /// </summary>
        Warm,

        /// <summary>Nothing of the file in memory; see <see cref="PageCache"/>. Linux only.</summary>
        Cold,
    }

    /// <summary>The cache states a benchmark can be run in here.</summary>
    internal static class CacheStates
    {
        /// <summary>
        /// Both on Linux; elsewhere only warm, since dropping a file from the page cache is only
        /// implemented there, and a case that cannot be set up would fail the run.
        /// </summary>
        public static IEnumerable<CacheState> Available =>
            OperatingSystem.IsLinux() ? [CacheState.Warm, CacheState.Cold] : [CacheState.Warm];
    }

    /// <summary>
    /// A benchmark over a fresh copy of the seeded database, which by default lands a ping round before
    /// each iteration, as the running application would between two clicks; see
    /// <see cref="WorkingDatabase"/>. Startup overrides that, since a service starting has no round
    /// behind it.
    /// </summary>
    /// <remarks>
    /// Every iteration is one call. BenchmarkDotNet's monitoring strategy, set in
    /// <see cref="BenchmarkConfig"/>, times each one on its own and leaves the statistics to say how
    /// steady they were, which suits operations that take milliseconds to seconds and change the
    /// database they run against.
    /// </remarks>
    public abstract class DatabaseBenchmark
    {
        internal WorkingDatabase Database { get; private set; } = null!;

        [GlobalSetup]
        public async Task OpenWorkingDatabaseAsync()
        {
            Database = await WorkingDatabase.OpenAsync();
            await OnOpenedAsync();
        }

        [IterationSetup]
        public void PrepareIteration() => PrepareIterationAsync().GetAwaiter().GetResult();

        [IterationCleanup]
        public void FinishIteration() => FinishIterationAsync().GetAwaiter().GetResult();

        [GlobalCleanup]
        public async Task CloseWorkingDatabaseAsync() => await Database.DisposeAsync();

        private protected virtual Task OnOpenedAsync() => Task.CompletedTask;

        private protected virtual Task PrepareIterationAsync() => Database.LandRoundAsync();

        private protected virtual Task FinishIterationAsync() => Task.CompletedTask;
    }

    /// <summary>A read measured both with the database in the page cache and without it.</summary>
    public abstract class CacheStateBenchmark : DatabaseBenchmark
    {
        public static IEnumerable<CacheState> CacheStateValues => CacheStates.Available;

        [ParamsSource(nameof(CacheStateValues))]
        public CacheState Cache { get; set; }

        private protected override async Task PrepareIterationAsync()
        {
            await Database.LandRoundAsync();
            if (Cache == CacheState.Cold)
            {
                Database.EvictFromPageCache();
            }
        }
    }

    /// <summary>The heading and the sentence the results summary gives a benchmark class.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class PageBenchmarkAttribute(string heading, string description) : Attribute
    {
        public string Heading { get; } = heading;

        public string Description { get; } = description;
    }
}
