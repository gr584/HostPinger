using BenchmarkDotNet.Attributes;
using HostPinger.Benchmarks.Infrastructure;
using HostPinger.Benchmarks.Seeding;
using HostPinger.Core.Options;
using HostPinger.Core.Services;

namespace HostPinger.Benchmarks.Benchmarks
{
    /// <summary>A change one of the pages makes.</summary>
    public enum PageWrite
    {
        /// <summary>The Hosts page adding a host.</summary>
        AddHost,

        /// <summary>The Hosts page saving an edited host.</summary>
        RenameHost,

        /// <summary>The Hosts page's pause switch, on the host that has been down longest.</summary>
        PauseOrResumeHost,

        /// <summary>The Hosts page deleting a host with no history; see <see cref="DeleteHostWithHistoryBenchmarks"/>.</summary>
        DeleteHost,

        /// <summary>
        /// The Configuration page saving its pinger settings. The page reads the database's figures
        /// again afterwards, off its circuit, which <see cref="ConfigurationPageBenchmarks"/> times.
        /// </summary>
        SaveSettings,
    }

    /// <summary>
    /// The writes the pages make through <see cref="HostStore"/> and <see cref="UserSettingsStore"/>,
    /// timed the way the page waits for them: a change to a host ends with the summary read again
    /// after it, through the summary's own loop, and the page draws that; a save of the settings ends
    /// when it is stored.
    /// </summary>
    /// <remarks>
    /// The page awaits these on its circuit, where every other click waits behind them. Before each
    /// one a round lands and the summary has read it, so the change starts with nothing else running
    /// and its own read of the summary is the only one it waits for.
    /// </remarks>
    [BenchmarkCategory("Writes")]
    [PageBenchmark(
        "Changes from the pages",
        "`HostStore` writes, each timed to the summary read after it that the Hosts page waits for, and a `UserSettingsStore` save of the Configuration page's pinger settings.")]
    public class PageWriteBenchmarks : DatabaseBenchmark
    {
        private const string AddedName = "added-by-benchmark";
        private const string AddedAddress = "added-by-benchmark.bench.invalid";

        private int _longestOutageId;
        private string _longestOutageAddress = null!;
        private int _deleting;
        private bool _flipped;
        private HostSummarySnapshot? _afterAdd;

        [ParamsAllValues]
        public PageWrite Write { get; set; }

        [Benchmark]
        public async Task<HostSummarySnapshot> Change()
        {
            switch (Write)
            {
                case PageWrite.AddHost:
                    return _afterAdd = Saved(await Database.Hosts.AddAsync(AddedName, AddedAddress, isEnabled: true));

                case PageWrite.RenameHost:
                    _flipped = !_flipped;
                    return Saved(await Database.Hosts.UpdateAsync(
                        _longestOutageId,
                        _flipped ? $"{SyntheticHistory.LongestOutage} (renamed)" : SyntheticHistory.LongestOutage,
                        _longestOutageAddress,
                        isEnabled: true));

                case PageWrite.PauseOrResumeHost:
                    _flipped = !_flipped;
                    return await Database.Hosts.SetEnabledAsync(_longestOutageId, isEnabled: !_flipped);

                case PageWrite.DeleteHost:
                    return await Database.Hosts.DeleteAsync(_deleting);

                // The four fields the page's form sends, with the timeout changed each time: a save of
                // the values already stored writes nothing at all, since EF finds no change to send.
                case PageWrite.SaveSettings:
                    _flipped = !_flipped;
                    await Database.Settings.SaveAsync(new PingerSettingsUpdate
                    {
                        IntervalSeconds = SyntheticHistory.IntervalSeconds,
                        TimeoutSeconds = SyntheticHistory.TimeoutSeconds + (_flipped ? 1 : 0),
                        ResolveTimeoutSeconds = SyntheticHistory.ResolveTimeoutSeconds,
                        RetryAttempts = SyntheticHistory.RetryAttempts,
                    });
                    return Database.Summaries.Current;

                default:
                    throw new ArgumentOutOfRangeException(nameof(Write), Write, null);
            }
        }

        private protected override async Task OnOpenedAsync()
        {
            var longest = Database.Host(SyntheticHistory.LongestOutage).Host;
            _longestOutageId = longest.Id;
            _longestOutageAddress = longest.Address;
            await Database.StartSummaryLoopAsync();
        }

        private protected override async Task PrepareIterationAsync()
        {
            await Database.LandRoundAsync();
            if (Write == PageWrite.DeleteHost)
            {
                _deleting = IdOf(Saved(await Database.Hosts.AddAsync(AddedName, AddedAddress, isEnabled: true)), AddedAddress);
            }

            // Waits for the loop to have read everything above, so it is idle when the change starts.
            await Database.Summaries.RefreshAsync();
        }

        private protected override async Task FinishIterationAsync()
        {
            if (Write == PageWrite.AddHost && _afterAdd is not null)
            {
                await Database.Hosts.DeleteAsync(IdOf(_afterAdd, AddedAddress));
                _afterAdd = null;
            }
        }

        private static int IdOf(HostSummarySnapshot summary, string address) =>
            summary.Rows.Single(row => row.Host.Address == address).Host.Id;

        /// <summary>A change that was refused would time the refusal, which is not what is being measured.</summary>
        private static HostSummarySnapshot Saved(HostChange change) =>
            change.Outcome == HostChangeOutcome.Saved
                ? change.Summary
                : throw new InvalidOperationException($"The change was not saved: {change.Outcome}.");
    }

    /// <summary>
    /// Deleting the host that has been down longest, with its forty-eight days of history: four
    /// hundred thousand attempts, interleaved through the whole table with every other host's, removed
    /// by the cascade and then the summary read again. The page awaits it on its circuit, and the
    /// write holds the database while it runs, so a ping round waits for it too.
    /// </summary>
    /// <remarks>
    /// A delete cannot be repeated on the same file, so every iteration starts from a fresh copy of the
    /// template, which is why there are fewer of them. Warm reads the whole file into the page cache
    /// first, so that the copy itself does not decide what is in memory: copying fills the cache on one
    /// filesystem and shares the blocks without reading them on another.
    /// </remarks>
    [BenchmarkCategory("Writes")]
    [PageBenchmark(
        "Deleting a host with its history",
        "`HostStore.DeleteAsync` on the host down for 41 days: the cascade over its 417,000 attempts, then the summary read after it.")]
    [WarmupCount(1)]
    [IterationCount(5)]
    public class DeleteHostWithHistoryBenchmarks
    {
        private WorkingDatabase? _database;
        private int _deleting;

        public static IEnumerable<CacheState> CacheStateValues => CacheStates.Available;

        [ParamsSource(nameof(CacheStateValues))]
        public CacheState Cache { get; set; }

        [GlobalSetup]
        public Task SeedAsync() => SeededTemplate.EnsureAsync(Console.Out);

        [IterationSetup]
        public void Restore() => RestoreAsync().GetAwaiter().GetResult();

        [Benchmark]
        public Task<HostSummarySnapshot> DeleteLongestOutage() => _database!.Hosts.DeleteAsync(_deleting);

        [GlobalCleanup]
        public async Task CloseAsync()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
                _database = null;
            }
        }

        private async Task RestoreAsync()
        {
            await CloseAsync();
            _database = await WorkingDatabase.OpenAsync();
            await _database.StartSummaryLoopAsync();
            _deleting = _database.Host(SyntheticHistory.LongestOutage).Host.Id;
            if (Cache == CacheState.Cold)
            {
                _database.EvictFromPageCache();
            }
            else
            {
                _database.LoadIntoPageCache();
            }
        }
    }
}
