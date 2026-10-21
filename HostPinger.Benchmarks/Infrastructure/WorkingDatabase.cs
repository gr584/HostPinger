using HostPinger.Benchmarks.Seeding;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HostPinger.Benchmarks.Infrastructure
{
    /// <summary>
    /// A fresh copy of the seeded template, with the services the pages call wired up over it the way
    /// Program wires them, and opened the way the application opens a database before serving a page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A copy per benchmark, so every one starts from the same clean database whatever the one before
    /// it wrote, and so the template itself is never opened by anything that could change it. The
    /// application would migrate and prune a file it was handed; the template is already at the
    /// latest migration and under its limit, so opening the copy changes nothing either.
    /// </para>
    /// <para>
    /// Between iterations a benchmark lands a ping round through the application's own
    /// <see cref="PingMonitorService.RunRoundAsync"/>, with every host answering as it last did — the
    /// two down hosts stay down, the rest answer with their last round trip — on a clock that steps
    /// one interval per round. That is what happens between two reads in the running application, and
    /// it matters to what a read costs: a commit from another connection empties SQLite's own page
    /// cache on every connection that held one, so a read that follows a round reads its pages from
    /// the operating system again, as the pages' reads do. Reading twice with nothing in between
    /// would measure SQLite's cache instead.
    /// </para>
    /// </remarks>
    internal sealed class WorkingDatabase : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SteppingClock _clock;
        private readonly PingMonitorService _monitor;
        private bool _summaryLoopStarted;

        private WorkingDatabase(string path, ServiceProvider services, SteppingClock clock)
        {
            Path = path;
            _services = services;
            _clock = clock;
            _monitor = services.GetRequiredService<PingMonitorService>();
            Factory = services.GetRequiredService<IDbContextFactory<HostPingerDbContext>>();
            Settings = services.GetRequiredService<UserSettingsStore>();
            Summaries = services.GetRequiredService<HostSummaryCache>();
            Hosts = services.GetRequiredService<HostStore>();
            Reads = services.GetRequiredService<DatabaseReads>();
        }

        public string Path { get; }

        public IDbContextFactory<HostPingerDbContext> Factory { get; }

        public UserSettingsStore Settings { get; }

        public HostSummaryCache Summaries { get; }

        public HostStore Hosts { get; }

        public DatabaseReads Reads { get; }

        /// <summary>The time of the last round landed, which is what every read takes as now.</summary>
        public DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

        /// <summary>The ids of <see cref="SyntheticHistory.ChartedHosts"/>.</summary>
        public IReadOnlySet<int> ChartedHosts =>
            SyntheticHistory.ChartedHosts.Select(name => Host(name).Host.Id).ToHashSet();

        /// <summary>
        /// Copies the template to the working file, replacing whatever an earlier benchmark left there,
        /// and opens it.
        /// </summary>
        public static async Task<WorkingDatabase> OpenAsync()
        {
            var template = await SeededTemplate.EnsureAsync(Console.Out);
            var path = System.IO.Path.Combine(SeededTemplate.DataDirectory, "working.db");
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(path);
            File.Copy(template, path);

            var clock = new SteppingClock();
            var pingSender = new ReplayPingSender();
            var services = new ServiceCollection()
                .AddLogging(logging => logging.AddProvider(new ConsoleWarnings()).SetMinimumLevel(LogLevel.Warning))
                .Configure<PingerOptions>(_ => { })
                .Configure<SecurityOptions>(_ => { })
                .AddSingleton(new PingerPaths(path))
                .AddSingleton<UserSettingsStore>()
                .AddHostPingerDatabase(new PingerPaths(path))
                .AddSingleton<IPingSender>(pingSender)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<DatabasePruner>()
                .AddSingleton<MaintenanceGate>()
                .AddSingleton<PingMonitorService>()
                .AddSingleton<HostSummaryCache>()
                .AddSingleton<HostStore>()
                .AddSingleton<DatabaseReads>()
                .BuildServiceProvider();

            var database = new WorkingDatabase(path, services, clock);
            await HostPingerDatabase.OpenAsync(services);

            var current = database.Summaries.Current;
            pingSender.ReplayLastOf(current);
            clock.Set(current.Rows.Max(row => row.Last?.TimestampUtc ?? DateTime.MinValue));
            return database;
        }

        /// <summary>The Hosts page's row for a host, as of the last summary read.</summary>
        public HostSummary Host(string name) => Summaries.Current.Rows.Single(row => row.Host.Name == name);

        /// <summary>A store of the settings of its own, as a freshly started application has.</summary>
        public UserSettingsStore NewSettingsStore() =>
            ActivatorUtilities.CreateInstance<UserSettingsStore>(_services);

        /// <summary>A summary of its own over <paramref name="settings"/>, with no connection open yet.</summary>
        public HostSummaryCache NewSummaryCache(UserSettingsStore settings) =>
            ActivatorUtilities.CreateInstance<HostSummaryCache>(_services, settings);

        /// <summary>One ping round, an interval after the last; see the remarks on this class.</summary>
        public async Task LandRoundAsync()
        {
            _clock.Advance(SyntheticHistory.Interval);
            await _monitor.RunRoundAsync();
        }

        /// <summary>
        /// Starts the summary's own loop, which is what answers <see cref="HostSummaryCache.RefreshAsync"/>
        /// and so every change <see cref="HostStore"/> makes.
        /// </summary>
        public async Task StartSummaryLoopAsync()
        {
            await Summaries.StartAsync(CancellationToken.None);
            _summaryLoopStarted = true;
        }

        /// <summary>
        /// Drops the file from the operating system's page cache and closes the pooled connections, so
        /// the next read finds nothing of the database in memory, as one does after a restart or once
        /// the rest of the machine has pushed it out. See <see cref="PageCache"/>.
        /// </summary>
        public void EvictFromPageCache()
        {
            SqliteConnection.ClearAllPools();
            PageCache.Evict(Path);
        }

        /// <summary>Reads the whole file once, so that every page of it is in the page cache.</summary>
        public void LoadIntoPageCache() => PageCache.Load(Path);

        public async ValueTask DisposeAsync()
        {
            if (_summaryLoopStarted)
            {
                await Summaries.StopAsync(CancellationToken.None);
            }

            await _services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(Path);
        }

        private static void DeleteDatabaseFiles(string path)
        {
            foreach (var suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }
}
