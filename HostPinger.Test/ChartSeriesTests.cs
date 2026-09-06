using HostPinger.Core.Charting;
using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    /// <summary>
    /// Holds <see cref="ChartSeries"/> to <see cref="ChartMath.Downsample"/>. The reduction moved
    /// into the database for speed, which left the rules for it written down twice; these read the
    /// same attempts both ways and compare, so the two cannot drift apart without a red run.
    /// </summary>
    public class ChartSeriesTests
    {
        private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private string _dbPath = string.Empty;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public async Task SetUp()
        {
            // A file rather than the shared in-memory connection the other tests use, because
            // ChartSeries opens and closes the connection around its aggregate.
            _dbPath = Path.Combine(Path.GetTempPath(), $"hostpinger-chart-{Guid.NewGuid():N}.db");
            _options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options;
            await using var db = new HostPingerDbContext(_options);
            await db.Database.EnsureCreatedAsync();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }

        /// <summary>
        /// The data is shaped to exercise every rule at once: buckets that average several pings,
        /// a stretch nothing was recorded over, and a stretch that was recorded and never answered.
        /// Those last two look identical in a row count and must not look identical on a chart.
        /// </summary>
        [Test]
        public async Task LoadAsync_ReducesExactlyAsTheInMemoryDownsamplerDoes()
        {
            const int bucketCount = 20;
            var rangeEnd = BaseTime.AddSeconds(1000);
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);

            var reduced = await ChartSeries.LoadAsync(db, hostId, BaseTime, rangeEnd, bucketCount);
            var expected = ChartMath.Downsample(await RawSamplesAsync(db, hostId, BaseTime, rangeEnd, bucketCount), BaseTime, rangeEnd, bucketCount);

            Assert.That(reduced, Is.EqualTo(expected));
            Assert.Multiple(() =>
            {
                Assert.That(reduced, Has.Count.LessThanOrEqualTo(bucketCount + 1), "the point of it is that a chart's worth comes back");
                Assert.That(reduced.Any(s => s.RoundtripMs is null), Is.True, "the unanswered stretch should read as down");
            });
        }

        /// <summary>
        /// Under the bucket count the attempts are handed back as they were recorded, keeping the
        /// exact ping times the hover readout shows rather than bucket midpoints.
        /// </summary>
        [Test]
        public async Task LoadAsync_HandsBackTheRecordedAttemptsWhenTheyFit()
        {
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);
            var rangeEnd = BaseTime.AddSeconds(1000);

            var reduced = await ChartSeries.LoadAsync(db, hostId, BaseTime, rangeEnd, bucketCount: 5000);

            var expected = await RawSamplesAsync(db, hostId, BaseTime, rangeEnd, 5000);
            Assert.That(reduced, Is.EqualTo(expected));
        }

        [Test]
        public async Task LoadAsync_ReadsNothingOutsideTheRange()
        {
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);

            var reduced = await ChartSeries.LoadAsync(db, hostId, BaseTime, BaseTime.AddSeconds(100), bucketCount: 10);

            Assert.That(reduced.Select(s => s.TimestampUtc),
                Is.All.LessThanOrEqualTo(BaseTime.AddSeconds(100)));
        }

        /// <summary>
        /// One attempt a second for 1000 seconds, less a stretch with nothing recorded in it and a
        /// stretch that went unanswered.
        /// </summary>
        private static async Task<int> SeedAsync(HostPingerDbContext db)
        {
            var host = new MonitoredHost { Name = "chart", Address = "chart.example", CreatedUtc = BaseTime };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();

            db.PingAttempts.AddRange(Enumerable.Range(0, 1000)
                .Where(i => i is < 300 or >= 400)
                .Select(i => new PingAttempt
                {
                    HostId = host.Id,
                    TimestampUtc = BaseTime.AddSeconds(i),
                    RoundtripMs = i is >= 500 and < 600 ? null : i % 50,
                }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return host.Id;
        }

        /// <summary>
        /// Every recorded attempt the chart would have read before the reduction moved into the
        /// database — from the start of the oldest bucket, which is what Downsample expects.
        /// </summary>
        private static async Task<IReadOnlyList<ChartSample>> RawSamplesAsync(
            HostPingerDbContext db,
            int hostId,
            DateTime rangeStart,
            DateTime rangeEnd,
            int bucketCount)
        {
            var queryStart = ChartMath.BucketStart(rangeStart, ChartMath.BucketDuration(rangeStart, rangeEnd, bucketCount));
            return await db.PingAttempts.AsNoTracking()
                .Where(a => a.HostId == hostId && a.TimestampUtc >= queryStart && a.TimestampUtc <= rangeEnd)
                .OrderBy(a => a.TimestampUtc)
                .Select(a => new ChartSample(a.TimestampUtc, a.RoundtripMs))
                .ToListAsync();
        }
    }
}
