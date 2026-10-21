using BenchmarkDotNet.Attributes;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>
    /// <see cref="HostPingerDatabase.OpenAsync(Microsoft.EntityFrameworkCore.IDbContextFactory{Core.Data.HostPingerDbContext}, UserSettingsStore, HostSummaryCache, CancellationToken)"/>,
    /// which Program used to run itself: the migrations, the settings and the first read of the
    /// summary, all before the first page is served.
    /// </summary>
    /// <remarks>
    /// With a settings store and a summary of their own each time and no pooled connections left over,
    /// as a process that has just started has none. Cold is the usual case here: a service starting
    /// after a boot finds nothing of its database in memory.
    /// </remarks>
    [BenchmarkCategory("Startup")]
    [PageBenchmark(
        "Startup",
        "`HostPingerDatabase.OpenAsync`: migrations, settings and the first Hosts summary, before the first page is served.")]
    public class StartupBenchmarks : DatabaseBenchmark
    {
        private UserSettingsStore _settings = null!;
        private HostSummaryCache _summaries = null!;

        public static IEnumerable<CacheState> CacheStateValues => CacheStates.Available;

        [ParamsSource(nameof(CacheStateValues))]
        public CacheState Cache { get; set; }

        [Benchmark]
        public async Task<HostSummarySnapshot> OpenDatabase()
        {
            await HostPingerDatabase.OpenAsync(Database.Factory, _settings, _summaries);
            return _summaries.Current;
        }

        private protected override Task PrepareIterationAsync()
        {
            SqliteConnection.ClearAllPools();
            _settings = Database.NewSettingsStore();
            _summaries = Database.NewSummaryCache(_settings);
            if (Cache == CacheState.Cold)
            {
                Database.EvictFromPageCache();
            }

            return Task.CompletedTask;
        }

        private protected override Task FinishIterationAsync()
        {
            _summaries.Dispose();
            return Task.CompletedTask;
        }
    }
}
