using BenchmarkDotNet.Attributes;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Ten ping rounds on a nearly full disk, beside ten on a disk with room. A round holding the
    /// database at its size saves, reads the file's size and, where the rows took it past what it
    /// was, takes them back out, deletes the oldest batch of history and saves them into the space
    /// that leaves; the rounds after it save into the rest of that space. Each iteration starts
    /// with nothing free inside the file, so the first of its rounds to need a page makes the room,
    /// as the first round does once the disk fills.
    /// </summary>
    [BenchmarkCategory("Prune")]
    [PageBenchmark(
        "Ten ping rounds on a nearly full disk",
        "`PingMonitorService.RunRoundAsync` ten times with under 64 MB free: the database is held at its size, and the oldest batch is deleted once the rows no longer fit.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class NearlyFullDiskBenchmarks : DatabaseBenchmark
    {
        private const int Rounds = 10;

        [Params(false, true)]
        public bool DiskNearlyFull { get; set; }

        [Benchmark]
        public async Task LandRounds()
        {
            for (var round = 0; round < Rounds; round++)
            {
                await Database.LandRoundAsync();
            }
        }

        private protected override Task OnOpenedAsync()
        {
            // For the whole run rather than per iteration, so the warning the first held round
            // logs is logged once.
            Database.FreeBytes = DiskNearlyFull ? 0 : null;
            return Task.CompletedTask;
        }

        private protected override async Task PrepareIterationAsync()
        {
            await Database.LandRoundAsync();

            // What an earlier iteration's deleting left free goes back to the disk.
            await using var db = await Database.Factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;");
        }
    }
}
