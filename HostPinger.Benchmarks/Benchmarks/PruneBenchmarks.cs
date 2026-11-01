using BenchmarkDotNet.Attributes;
using HostPinger.Core.Options;
using HostPinger.Core.Services;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>
    /// The ping round after the size limit has been lowered 100 MB below the file, which is what
    /// saving a smaller limit on the Configuration page, or restoring a backup whose limit this disk
    /// cannot hold, hands the next round. The round prunes for at most a quarter of the interval,
    /// 2.5 s at the seeded ten, and leaves the rest to the rounds after it; it used to go on until
    /// the file was under the new limit, about 40 s here at 2.5 MB a second, with no other round
    /// running meanwhile. Each iteration lowers the limit again from wherever the last one left
    /// the file.
    /// </summary>
    [BenchmarkCategory("Prune")]
    [PageBenchmark(
        "A ping round after the size limit is lowered",
        "`PingMonitorService.RunRoundAsync` with the limit set 100 MB below the file: the pruning it does is held to a quarter of the interval.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class LoweredLimitBenchmarks : DatabaseBenchmark
    {
        private const int CutMb = 100;

        [Benchmark]
        public Task LandRound() => Database.LandRoundAsync();

        private protected override async Task PrepareIterationAsync()
        {
            await Database.LandRoundAsync();

            long sizeBytes;
            await using (var db = await Database.Factory.CreateDbContextAsync())
            {
                sizeBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
            }

            await Database.Settings.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = (int)(sizeBytes >> 20) - CutMb });
        }
    }
}
