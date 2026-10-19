using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HostPinger.Test
{
    /// <summary>
    /// The reads the pages make through <see cref="DatabaseReads"/>. What each finds is settled
    /// where its query lives — GraphReadTests, ResolverErrorSummaryTests, DatabaseStatsTests — so
    /// these hold the service to handing each one through: the same answer, read through a context
    /// of its own that is given back when the read is done, so that no read leaves a connection
    /// out of the pool.
    /// </summary>
    public class DatabaseReadsTests
    {
        /// <summary>
        /// Years from the clock, so that a read counting back from the clock rather than from the
        /// moment it is given gives different counts.
        /// </summary>
        private static readonly DateTime Now = new(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        private string _dbPath = string.Empty;
        private DbContextOptions<HostPingerDbContext> _options = null!;
        private CountingFactory _factory = null!;
        private DatabaseReads _reads = null!;
        private int _hostId;

        /// <summary>The three reads, for what holds of all of them.</summary>
        public enum Read
        {
            Graph,
            ResolverErrors,
            Stats,
        }

        [SetUp]
        public async Task SetUp()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"hostpinger-reads-{Guid.NewGuid():N}.db");
            _options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options;
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.Database.EnsureCreatedAsync();
                var host = new MonitoredHost { Name = "alpha", Address = "alpha.example", CreatedUtc = Now };
                db.Hosts.Add(host);
                await db.SaveChangesAsync();
                _hostId = host.Id;
                db.PingAttempts.AddRange(Enumerable.Range(0, 300).Select(i => new PingAttempt
                {
                    HostId = host.Id,
                    TimestampUtc = Now.AddMinutes(-300 + i),
                    RoundtripMs = i % 7 == 0 ? null : 20 + i % 3,
                }));
                db.ResolverErrors.AddRange(
                    new ResolverError { Address = "gone.example", TimestampUtc = Now.AddHours(-2), Reason = ResolverFailure.TimedOut },
                    new ResolverError { Address = "gone.example", TimestampUtc = Now.AddDays(-3), Reason = ResolverFailure.NoAddresses });
                await db.SaveChangesAsync();
            }

            _factory = new CountingFactory(_options);
            _reads = new DatabaseReads(_factory);
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }

        /// <summary>The graph as GraphRead reads it, through one context that is given back.</summary>
        [Test]
        public async Task ReadGraphAsync_HandsBackWhatGraphReadReadsThroughAContextOfItsOwn()
        {
            var read = await _reads.ReadGraphAsync(new HashSet<int> { _hostId }, null, Now.AddHours(-5), Now, 50);

            await using var db = new HostPingerDbContext(_options);
            var expected = await GraphRead.LoadAsync(db, new HashSet<int> { _hostId }, null, Now.AddHours(-5), Now, 50);
            Assert.Multiple(() =>
            {
                Assert.That(read.Hosts.Select(h => h.Id), Is.EqualTo(expected.Hosts.Select(h => h.Id)));
                Assert.That(read.SeriesReads[_hostId].Samples, Is.EqualTo(expected.SeriesReads[_hostId].Samples));
                Assert.That(read.SeriesReads[_hostId].Samples, Is.Not.Empty);
                Assert.That(_factory.Created, Is.EqualTo(1));
                Assert.That(_factory.Disposed, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The resolver errors as their summary reads them, counted back from the moment given
        /// rather than from the clock, through one context that is given back.
        /// </summary>
        [Test]
        public async Task ReadResolverErrorsAsync_HandsBackWhatTheSummaryReadsThroughAContextOfItsOwn()
        {
            var rows = await _reads.ReadResolverErrorsAsync(Now);

            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(rows, Is.EqualTo(await ResolverErrorSummary.LoadAsync(db, Now)));
                Assert.That(rows.Single().Last24Hours, Is.EqualTo(1), "read at the moment it was given");
                Assert.That(_factory.Created, Is.EqualTo(1));
                Assert.That(_factory.Disposed, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The database's figures as the stats collect them, projected against the settings the
        /// page passes in — neither of them the default — through one context that is given back.
        /// </summary>
        [Test]
        public async Task ReadStatsAsync_HandsBackWhatTheStatsCollectThroughAContextOfItsOwn()
        {
            var options = new PingerOptions { IntervalSeconds = 45, MaxDatabaseSizeMb = 64 };

            var stats = await _reads.ReadStatsAsync(options);

            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(stats, Is.EqualTo(await DatabaseStats.CollectAsync(db, options)));
                Assert.That(stats.AttemptCount, Is.EqualTo(300));
                Assert.That(stats.IntervalSeconds, Is.EqualTo(45), "projected against the settings given");
                Assert.That(stats.MaxSizeBytes, Is.EqualTo(64L * 1024 * 1024));
                Assert.That(_factory.Created, Is.EqualTo(1));
                Assert.That(_factory.Disposed, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// A refresh carries on from the series on screen, which the graph page hands back in; told
        /// apart from a fresh read by deleting rows behind the earlier read's newest point.
        /// </summary>
        [Test]
        public async Task ReadGraphAsync_CarriesOnFromTheEarlierReadItIsGiven()
        {
            var plotted = new HashSet<int> { _hostId };
            var earlier = await _reads.ReadGraphAsync(plotted, null, Now.AddHours(-5), Now, 50);
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.PingAttempts.Where(a => a.TimestampUtc < Now.AddHours(-4)).ExecuteDeleteAsync();
            }

            var carried = await _reads.ReadGraphAsync(plotted, earlier.SeriesReads, Now.AddHours(-5), Now, 50);
            var fresh = await _reads.ReadGraphAsync(plotted, null, Now.AddHours(-5), Now, 50);

            Assert.Multiple(() =>
            {
                Assert.That(carried.SeriesReads[_hostId].Samples, Is.EqualTo(earlier.SeriesReads[_hostId].Samples));
                Assert.That(fresh.SeriesReads[_hostId].Samples, Is.Not.EqualTo(earlier.SeriesReads[_hostId].Samples));
            });
        }

        /// <summary>
        /// A read cancelled by the page that sent it stops with the cancellation — the page's
        /// token reaches it — and still gives its context back, which is the read leaving a graph
        /// makes on every navigation away from it.
        /// </summary>
        [TestCase(Read.Graph)]
        [TestCase(Read.ResolverErrors)]
        [TestCase(Read.Stats)]
        public void Reads_CancelledByThePage_StopAndGiveTheirContextBack(Read read)
        {
            Assert.That(
                async () => await ReadAsync(_reads, read, new CancellationToken(canceled: true)),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.Multiple(() =>
            {
                Assert.That(_factory.Created, Is.EqualTo(1));
                Assert.That(_factory.Disposed, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// Each read is over by the time the call returns, through the factory the application
        /// registers. That is what lets a page's first read stay on its circuit and be drawn in the
        /// same render: a read that let go of the thread partway would have the page draw its empty
        /// state over the prerendered one, which PageReads exists to prevent.
        /// </summary>
        [TestCase(Read.Graph)]
        [TestCase(Read.ResolverErrors)]
        [TestCase(Read.Stats)]
        public async Task Reads_AreOverByTheTimeTheCallReturns(Read read)
        {
            await using var services = new ServiceCollection()
                .AddDbContextFactory<HostPingerDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"))
                .BuildServiceProvider();
            var reads = new DatabaseReads(services.GetRequiredService<IDbContextFactory<HostPingerDbContext>>());

            var reading = ReadAsync(reads, read, CancellationToken.None);

            Assert.That(reading.IsCompleted, Is.True);
            await reading;
        }

        private Task ReadAsync(DatabaseReads reads, Read read, CancellationToken cancellationToken) => read switch
        {
            Read.Graph => reads.ReadGraphAsync(new HashSet<int> { _hostId }, null, Now.AddHours(-5), Now, 50, cancellationToken),
            Read.ResolverErrors => reads.ReadResolverErrorsAsync(Now, cancellationToken),
            _ => reads.ReadStatsAsync(new PingerOptions { IntervalSeconds = 45 }, cancellationToken),
        };

        /// <summary>Hands out contexts and counts them out and back in.</summary>
        private sealed class CountingFactory(DbContextOptions<HostPingerDbContext> options) : IDbContextFactory<HostPingerDbContext>
        {
            public int Created { get; private set; }

            public int Disposed { get; private set; }

            public HostPingerDbContext CreateDbContext()
            {
                Created++;
                return new CountedContext(options, () => Disposed++);
            }
        }

        private sealed class CountedContext(DbContextOptions<HostPingerDbContext> options, Action disposed)
            : HostPingerDbContext(options)
        {
            private bool _counted;

            public override void Dispose()
            {
                Count();
                base.Dispose();
            }

            public override ValueTask DisposeAsync()
            {
                Count();
                return base.DisposeAsync();
            }

            private void Count()
            {
                if (!_counted)
                {
                    _counted = true;
                    disposed();
                }
            }
        }
    }
}
