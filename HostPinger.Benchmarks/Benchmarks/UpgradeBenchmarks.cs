using BenchmarkDotNet.Attributes;
using HostPinger.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>
    /// The first start of a new release on a production-sized database: bringing it forward from
    /// the schema the last release left. Every other benchmark seeds at the latest migration and so
    /// never sees an upgrade, which is how the 1.12.0 upgrade's two index rebuilds outlasting
    /// systemd's start timeout went unnoticed until after 1.13.0 had shipped.
    /// </summary>
    /// <remarks>
    /// Before each iteration, untimed, the working copy is taken back to
    /// <see cref="LastReleasedMigration"/> through the migrations' own Down steps; the iteration
    /// times bringing it forward again. While nothing has been added since the last release there is
    /// nothing to undo or apply, and this measures only the check that says so. The working copy is
    /// in WAL mode by then, where a real first start migrates in rollback-journal mode and switches
    /// afterwards; that changes the figure somewhat, not whether it is seconds or minutes.
    /// </remarks>
    [BenchmarkCategory("Upgrade")]
    [PageBenchmark(
        "Upgrading from the last release",
        "`HostPingerDatabase.InitializeAsync` on the database taken back to the schema the last release left: every migration since, as the first start of a new release runs them.")]
    [WarmupCount(1)]
    [IterationCount(3)]
    public class UpgradeBenchmarks : DatabaseBenchmark
    {
        /// <summary>
        /// The last migration the latest release shipped: 1.13.0's. Moved on when a release is
        /// tagged, so that this goes on timing the upgrade the next release will make.
        /// </summary>
        internal const string LastReleasedMigration = "20261016020813_AddRoundtripToPartialPingIndexes";

        [Benchmark]
        public async Task<bool> MigrateFromLastRelease()
        {
            await using var db = await Database.Factory.CreateDbContextAsync();
            return await HostPingerDatabase.InitializeAsync(db);
        }

        private protected override async Task PrepareIterationAsync()
        {
            await using var db = await Database.Factory.CreateDbContextAsync();
            await db.GetService<IMigrator>().MigrateAsync(LastReleasedMigration);
        }
    }
}
