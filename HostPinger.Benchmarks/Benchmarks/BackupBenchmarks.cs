using BenchmarkDotNet.Attributes;
using HostPinger.Core.Services;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>
    /// Writing the snapshot a backup download hands over, <see cref="DatabaseBackup.CreateSnapshotAsync"/>:
    /// VACUUM INTO over the whole database, which the Configuration page waits on with its bar before
    /// the browser is given anything. Cold is the usual case, since a backup is rarely taken of a
    /// database anything has read lately.
    /// </summary>
    /// <remarks>
    /// Each iteration writes a gigabyte beside the working file and deletes it afterwards, so there are
    /// fewer of them than the default.
    /// </remarks>
    [BenchmarkCategory("Backup")]
    [PageBenchmark(
        "Writing a backup",
        "`DatabaseBackup.CreateSnapshotAsync`: the compacted copy a backup download hands over, written beside the database.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class SnapshotBenchmarks : CacheStateBenchmark
    {
        private string? _snapshot;

        [Benchmark]
        public async Task<string> CreateSnapshot() => _snapshot = await Database.Backup.CreateSnapshotAsync();

        private protected override Task FinishIterationAsync()
        {
            if (_snapshot is not null)
            {
                File.Delete(_snapshot);
                _snapshot = null;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A ping round landing while a backup's snapshot is being written. The snapshot used to hold the
    /// rounds off for the whole copy, which put the copy's length into this figure and the same gap
    /// into every host's history; in WAL mode the round now lands as it would with nothing else
    /// running. Each iteration starts a snapshot and waits for it to be under way, then times the
    /// round, then waits for the snapshot and deletes it.
    /// </summary>
    [BenchmarkCategory("Backup")]
    [PageBenchmark(
        "A ping round while a backup is written",
        "`PingMonitorService.RunRoundAsync` with a `CreateSnapshotAsync` copy under way: the round should not wait for the copy.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class RoundDuringSnapshotBenchmarks : DatabaseBenchmark
    {
        private Task<string>? _snapshot;

        [Benchmark]
        public Task LandRound() => Database.LandRoundAsync();

        private protected override async Task PrepareIterationAsync()
        {
            var underWay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshot = Database.Backup.CreateSnapshotAsync(new Reports(fraction =>
            {
                if (fraction > 0)
                {
                    underWay.TrySetResult();
                }
            }));

            // A snapshot that fails before it reports anything would leave the wait hanging.
            await Task.WhenAny(underWay.Task, _snapshot);
            if (_snapshot.IsCompleted)
            {
                File.Delete(await _snapshot);
                throw new InvalidOperationException("The snapshot finished before the round could land during it.");
            }
        }

        private protected override async Task FinishIterationAsync()
        {
            if (_snapshot is not null)
            {
                File.Delete(await _snapshot);
                _snapshot = null;
            }
        }

        /// <summary>Hears each report on the thread that makes it, rather than posted somewhere later.</summary>
        private sealed class Reports(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }
    }

    /// <summary>
    /// Checking an uploaded backup before anything is replaced, <see cref="DatabaseBackup.ValidateBackupAsync"/>:
    /// SQLite's integrity check over the whole file, then its schema and migrations. The page shows a
    /// bar without a measure meanwhile, and nothing else waits on it. Warm only: the file checked is
    /// the upload just written.
    /// </summary>
    [BenchmarkCategory("Restore")]
    [PageBenchmark(
        "Checking a backup",
        "`DatabaseBackup.ValidateBackupAsync` on a snapshot of the database: the integrity check, the schema and the migrations.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class ValidateBackupBenchmarks : DatabaseBenchmark
    {
        private string _candidate = null!;

        [Benchmark]
        public Task<string?> ValidateBackup() => Database.Backup.ValidateBackupAsync(_candidate);

        private protected override async Task OnOpenedAsync() => _candidate = await Database.Backup.CreateSnapshotAsync();
    }

    /// <summary>
    /// Putting a checked backup in place, <see cref="DatabaseBackup.RestoreAsync"/>: the current backup
    /// made ready beside the database, then the swap with the rounds held off and the pages kept from
    /// the file, then the restored file opened and its settings loaded. A backup of the current schema
    /// needs no migrating, so this is all of a restore that holds anything up. Each iteration restores
    /// a fresh copy of one snapshot, which the swap consumes.
    /// </summary>
    [BenchmarkCategory("Restore")]
    [PageBenchmark(
        "Restoring a backup",
        "`DatabaseBackup.RestoreAsync` of a current backup: made ready, swapped in, opened, its settings loaded.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class RestoreBenchmarks : DatabaseBenchmark
    {
        private string _snapshot = null!;
        private string _candidate = null!;

        [Benchmark]
        public Task Restore() => Database.Backup.RestoreAsync(_candidate);

        private protected override async Task OnOpenedAsync() => _snapshot = await Database.Backup.CreateSnapshotAsync();

        private protected override async Task PrepareIterationAsync()
        {
            await Database.LandRoundAsync();
            _candidate = Database.Backup.CreateRestoreTempPath();
            File.Copy(_snapshot, _candidate);
        }
    }
}
