using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    /// <summary>
    /// The host summary the Hosts page draws, held in memory and read again whenever the database
    /// changes. Against a real file, because what tells it about a change — SQLite's data_version —
    /// only means anything between separate connections to one.
    /// </summary>
    public class HostSummaryCacheTests
    {
        private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private string _directory = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;
        private TestOptionsMonitor<PingerOptions> _pinger = null!;
        private HostSummaryCache _cache = null!;

        [SetUp]
        public async Task SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-summary-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_directory, "hostpinger.db")}")
                .Options;
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            _pinger = new TestOptionsMonitor<PingerOptions>(new PingerOptions { RetryAttempts = 3 });
            var store = new UserSettingsStore(
                new TestDb.Factory(_options),
                _pinger,
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
            _cache = new HostSummaryCache(new TestDb.Factory(_options), store, NullLogger<HostSummaryCache>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            _cache.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        /// <summary>What the first page after a start draws: the hosts as they stand, read before it asks.</summary>
        [Test]
        public async Task LoadAsync_ReadsTheHostsAsTheyStand()
        {
            await AddHostAsync("alpha", 12);

            await _cache.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(Names(_cache.Current), Is.EqualTo(new[] { "alpha" }));
                Assert.That(_cache.Current.Rows[0].Status, Is.EqualTo(HostStatus.Up));
            });
        }

        /// <summary>
        /// A ping round, an edit, or a test seeding rows from outside the process all commit on a
        /// connection of their own, and that is what the next turn of the loop notices and reads.
        /// </summary>
        [Test]
        public async Task PollAsync_ReadsAgainOnceAnotherConnectionHasCommitted()
        {
            await AddHostAsync("alpha", 12);
            await _cache.LoadAsync();
            var told = new List<HostSummarySnapshot>();
            _cache.Updated += told.Add;

            await AddHostAsync("bravo", 15);
            await _cache.PollAsync();

            Assert.Multiple(() =>
            {
                Assert.That(Names(_cache.Current), Is.EqualTo(new[] { "alpha", "bravo" }));
                Assert.That(told, Is.EqualTo(new[] { _cache.Current }), "the pages are told, once");
            });
        }

        /// <summary>
        /// The other side of it: while nothing commits, a turn of the loop reads nothing, and the
        /// pages are not told about a summary that has not changed.
        /// </summary>
        [Test]
        public async Task PollAsync_LeavesTheSummaryAloneWhileNothingHasChanged()
        {
            await AddHostAsync("alpha", 12);
            await _cache.LoadAsync();
            var before = _cache.Current;
            var told = 0;
            _cache.Updated += _ => told++;

            await _cache.PollAsync();
            await _cache.PollAsync();

            Assert.Multiple(() =>
            {
                Assert.That(_cache.Current, Is.SameAs(before));
                Assert.That(told, Is.Zero);
            });
        }

        /// <summary>
        /// The retry count changes what the same rows mean without anything being written, so a
        /// change to it is read again for: one missed ping is a retry at three retries and an
        /// outage at none.
        /// </summary>
        [Test]
        public async Task PollAsync_ReadsAgainWhenTheRetryCountChanges()
        {
            await AddHostAsync("flaky", 12, null);
            await _cache.LoadAsync();
            Assert.That(_cache.Current.Rows[0].Status, Is.EqualTo(HostStatus.Retrying), "at three retries");

            _pinger.CurrentValue = new PingerOptions { RetryAttempts = 0 };
            await _cache.PollAsync();

            Assert.Multiple(() =>
            {
                Assert.That(_cache.Current.MissesToDown, Is.EqualTo(1));
                Assert.That(_cache.Current.Rows[0].Status, Is.EqualTo(HostStatus.Down));
            });
        }

        /// <summary>
        /// What HostStore waits on after a change: a read taken after it asked, which is what makes
        /// the summary it hands the page hold that change.
        /// </summary>
        [Test]
        public async Task RefreshAsync_HandsBackAReadTakenAfterItWasAsked()
        {
            await AddHostAsync("alpha", 12);
            await _cache.LoadAsync();
            await AddHostAsync("added", 20);

            var refresh = _cache.RefreshAsync();
            Assert.That(refresh.IsCompleted, Is.False, "nothing has read since it asked");
            await _cache.PollAsync();

            Assert.That(Names(await refresh.WaitAsync(Patience)), Is.EqualTo(new[] { "added", "alpha" }));
        }

        /// <summary>
        /// A read that fails leaves the summary in hand standing, and HostStore, waiting on a read
        /// after a change, is given that rather than an error to pass up; the failure goes to the
        /// loop, which logs it.
        /// </summary>
        [Test]
        public async Task PollAsync_KeepsTheSummaryInHandWhenAReadFails()
        {
            await AddHostAsync("alpha", 12);
            await _cache.LoadAsync();
            var before = _cache.Current;
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE PingAttempts;");
            }

            var refresh = _cache.RefreshAsync();

            Assert.That(async () => await _cache.PollAsync(), Throws.InstanceOf<SqliteException>());
            Assert.Multiple(async () =>
            {
                Assert.That(await refresh.WaitAsync(Patience), Is.SameAs(before));
                Assert.That(_cache.Current, Is.SameAs(before));
            });
        }

        /// <summary>
        /// Suspended for a restore, it reads nothing, whatever is committed meanwhile, and the pages
        /// keep what they have; resumed, it reads afresh.
        /// </summary>
        [Test]
        public async Task SuspendAsync_HoldsOffReadingUntilResumed()
        {
            await AddHostAsync("alpha", 12);
            await _cache.LoadAsync();
            var before = _cache.Current;

            await _cache.SuspendAsync();
            await AddHostAsync("bravo", 15);
            await _cache.PollAsync();
            var whileSuspended = _cache.Current;
            _cache.Resume();
            await _cache.PollAsync();

            Assert.Multiple(() =>
            {
                Assert.That(whileSuspended, Is.SameAs(before));
                Assert.That(Names(_cache.Current), Is.EqualTo(new[] { "alpha", "bravo" }));
            });
        }

        /// <summary>
        /// Pages are told one at a time, so one whose circuit has gone wrong cannot keep the new
        /// summary from the rest.
        /// </summary>
        [Test]
        public async Task PollAsync_TellsEveryPageEvenWhenOneFails()
        {
            await _cache.LoadAsync();
            var told = new List<HostSummarySnapshot>();
            _cache.Updated += _ => throw new InvalidOperationException("a page that has gone wrong");
            _cache.Updated += told.Add;

            await AddHostAsync("alpha", 12);
            await _cache.PollAsync();

            Assert.That(told, Has.Count.EqualTo(1));
        }

        /// <summary>The loop itself: running on its own, it notices a commit and tells the pages within moments.</summary>
        [Test]
        public async Task Loop_TellsThePagesOfACommitOnItsOwn()
        {
            await _cache.LoadAsync();
            var seen = new TaskCompletionSource<HostSummarySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cache.Updated += summary =>
            {
                if (Names(summary).Contains("alpha"))
                {
                    seen.TrySetResult(summary);
                }
            };

            await _cache.StartAsync(CancellationToken.None);
            try
            {
                await AddHostAsync("alpha", 12);
                Assert.That(Names(await seen.Task.WaitAsync(Patience)), Does.Contain("alpha"));
            }
            finally
            {
                await _cache.StopAsync(CancellationToken.None);
            }
        }

        private static IEnumerable<string> Names(HostSummarySnapshot summary) => summary.Rows.Select(r => r.Host.Name);

        /// <summary>A host with one attempt a minute, answered or not, written on a connection of its own.</summary>
        private async Task AddHostAsync(string name, params int?[] roundtrips)
        {
            await using var db = new HostPingerDbContext(_options);
            var host = new MonitoredHost { Name = name, Address = $"{name}.example", CreatedUtc = Start };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            db.PingAttempts.AddRange(roundtrips.Select((roundtrip, i) => new PingAttempt
            {
                HostId = host.Id,
                TimestampUtc = Start.AddMinutes(i),
                RoundtripMs = roundtrip,
            }));
            await db.SaveChangesAsync();
        }
    }
}
