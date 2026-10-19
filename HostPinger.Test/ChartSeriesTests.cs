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
        /// <summary>
        /// A range that ends where it starts, or before, and a chart with no room for a point, are
        /// mistakes in the caller rather than empty charts, and are said to be.
        /// </summary>
        [Test]
        public async Task LoadAsync_RefusesARangeThatIsNoRangeAndAChartWithNoPoints()
        {
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);

            Assert.Multiple(() =>
            {
                Assert.That(
                    async () => await ChartSeries.LoadAsync(db, hostId, BaseTime, BaseTime, 20),
                    Throws.ArgumentException.With.Property(nameof(ArgumentException.ParamName)).EqualTo("rangeEndUtc"));
                Assert.That(
                    async () => await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(10), BaseTime, 20),
                    Throws.ArgumentException.With.Property(nameof(ArgumentException.ParamName)).EqualTo("rangeEndUtc"));
                Assert.That(
                    async () => await ChartSeries.LoadAsync(db, hostId, BaseTime, BaseTime.AddSeconds(10), 0),
                    Throws.InstanceOf<ArgumentOutOfRangeException>());
            });
        }

        [Test]
        public async Task LoadAsync_ReducesExactlyAsTheInMemoryDownsamplerDoes()
        {
            const int bucketCount = 20;
            var rangeEnd = BaseTime.AddSeconds(1000);
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);

            var reduced = (await ChartSeries.LoadAsync(db, hostId, BaseTime, rangeEnd, bucketCount)).Samples;
            var expected = ChartMath.Downsample(await RawSamplesAsync(db, hostId, BaseTime, rangeEnd, bucketCount), BaseTime, rangeEnd, bucketCount);

            Assert.That(reduced, Is.EqualTo(expected));
            Assert.Multiple(() =>
            {
                Assert.That(reduced, Has.Count.LessThanOrEqualTo(bucketCount + 1), "the point of it is that a chart's worth comes back");
                Assert.That(reduced.Any(s => s.RoundtripMs is null), Is.True, "the unanswered stretch should read as down");
            });
        }

        /// <summary>
        /// A bucket holding answered and missed pings alike averages the answered ones and counts
        /// as recorded, as the in-memory downsampler has it. The bucket query counts the two kinds
        /// apart, so a bucket across the edge of an outage is where it would go wrong; seven buckets
        /// over the thousand seconds are longer than the unanswered stretch, which cannot fill one,
        /// so every bucket it reaches into holds answered pings as well.
        /// </summary>
        [Test]
        public async Task LoadAsync_AveragesTheAnsweredPingsOfABucketThatAlsoMissedSome()
        {
            const int bucketCount = 7;
            var rangeEnd = BaseTime.AddSeconds(1000);
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);

            var reduced = (await ChartSeries.LoadAsync(db, hostId, BaseTime, rangeEnd, bucketCount)).Samples;
            var expected = ChartMath.Downsample(await RawSamplesAsync(db, hostId, BaseTime, rangeEnd, bucketCount), BaseTime, rangeEnd, bucketCount);

            Assert.That(reduced, Is.EqualTo(expected));
        }

        /// <summary>
        /// A bucket is counted and summed from the two partial indexes alone. Read from the
        /// attempts themselves, every round trip in the range was a fetch from the table, which on
        /// a cold cache is what kept a long graph from opening for most of a minute.
        /// </summary>
        [Test]
        public async Task BucketSql_ReadsNothingButTheTwoPartialIndexes()
        {
            await using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + ChartSeries.BucketSql;
            command.Parameters.AddWithValue("@hostId", 1);
            command.Parameters.AddWithValue("@from", BaseTime);
            command.Parameters.AddWithValue("@to", BaseTime.AddMinutes(1));
            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(3));
                }
            }

            var onAttempts = plan.Where(step => step.Contains("PingAttempts")).ToList();
            var detail = string.Join(" | ", plan);
            Assert.Multiple(() =>
            {
                Assert.That(onAttempts, Has.Count.EqualTo(2), $"One step for each half. Plan was: {detail}");
                Assert.That(onAttempts, Has.All.Contains("USING COVERING INDEX"), $"Neither half should touch the table. Plan was: {detail}");
                Assert.That(detail, Does.Contain("IX_PingAttempts_Answered").And.Contain("IX_PingAttempts_Unanswered"),
                    $"Each half should read its own partial index. Plan was: {detail}");
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

            var reduced = (await ChartSeries.LoadAsync(db, hostId, BaseTime, rangeEnd, bucketCount: 5000)).Samples;

            var expected = await RawSamplesAsync(db, hostId, BaseTime, rangeEnd, 5000);
            Assert.That(reduced, Is.EqualTo(expected));
        }

        [Test]
        public async Task LoadAsync_ReadsNothingOutsideTheRange()
        {
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db);

            var reduced = (await ChartSeries.LoadAsync(db, hostId, BaseTime, BaseTime.AddSeconds(100), bucketCount: 10)).Samples;

            Assert.That(reduced.Select(s => s.TimestampUtc),
                Is.All.LessThanOrEqualTo(BaseTime.AddSeconds(100)));
        }

        /// <summary>
        /// What the graph does every five seconds: a window sliding on while attempts go on being
        /// recorded, carried on each time from the read before. Every step has to come back exactly
        /// as a read from scratch over the same range would, across bucket boundaries at both ends,
        /// with the stretch nothing was recorded over sliding out and the unanswered one still in.
        /// </summary>
        [Test]
        public async Task RefreshAsync_CarriesOnExactlyAsAReadFromScratchWould()
        {
            const int bucketCount = 20;
            var window = TimeSpan.FromSeconds(600);
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db, seconds: 700);
            var rangeEnd = BaseTime.AddSeconds(700);
            var carried = await ChartSeries.LoadAsync(db, hostId, rangeEnd - window, rangeEnd, bucketCount);

            // Five seconds a step, which crosses into a new bucket every sixth step.
            for (var second = 700; second < 1000; second += 5)
            {
                await RecordAsync(db, hostId, second, second + 5);
                rangeEnd = BaseTime.AddSeconds(second + 5);
                carried = await ChartSeries.RefreshAsync(db, hostId, carried, rangeEnd - window, rangeEnd, bucketCount);
                var fresh = await ChartSeries.LoadAsync(db, hostId, rangeEnd - window, rangeEnd, bucketCount);

                Assert.That(carried.Samples, Is.EqualTo(fresh.Samples), $"carried on to {second + 5} s");
            }

            Assert.That(carried.BucketDuration, Is.EqualTo(TimeSpan.FromSeconds(30)), "still on the grid it started on");
        }

        /// <summary>
        /// The other side of carrying on: the buckets behind the newest one a read holds are not
        /// looked at again. A bucket emptied after the read stays in the carried-on series and is
        /// gone from a read from scratch, which is why the graph still reads from scratch every few
        /// minutes.
        /// </summary>
        [Test]
        public async Task RefreshAsync_ReadsOnlyFromTheNewestBucketItHolds()
        {
            const int bucketCount = 20;
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db, seconds: 700);
            var earlier = await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(100), BaseTime.AddSeconds(700), bucketCount);
            await DeleteAsync(db, hostId, 450, 480);
            await RecordAsync(db, hostId, 700, 705);

            var carried = await ChartSeries.RefreshAsync(db, hostId, earlier, BaseTime.AddSeconds(105), BaseTime.AddSeconds(705), bucketCount);
            var fresh = await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(105), BaseTime.AddSeconds(705), bucketCount);

            // Where the emptied bucket, 450 s to 480 s, reports.
            var emptied = BaseTime.AddSeconds(465);
            Assert.Multiple(() =>
            {
                Assert.That(carried.Samples.Select(s => s.TimestampUtc), Does.Contain(emptied));
                Assert.That(fresh.Samples.Select(s => s.TimestampUtc), Does.Not.Contain(emptied));
            });
        }

        /// <summary>
        /// A read on another grid, one that does not reach back as far as the new range, one that
        /// ran on past where the new range ends, and attempts handed back as they were recorded
        /// cannot be carried on, and each is read from scratch instead. A bucket emptied behind
        /// their backs is what shows it, since only a read from scratch sees it gone.
        /// </summary>
        [Test]
        public async Task RefreshAsync_ReadsFromScratchWhatItCannotCarryOn()
        {
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db, seconds: 700);
            var start = BaseTime.AddSeconds(100);
            var end = BaseTime.AddSeconds(700);
            var bucketed = await ChartSeries.LoadAsync(db, hostId, start, end, bucketCount: 20);
            var recorded = await ChartSeries.LoadAsync(db, hostId, start, end, bucketCount: 5000);
            await DeleteAsync(db, hostId, 450, 480);

            async Task AssertReadFromScratch(
                string reason,
                ChartSeriesRead earlier,
                DateTime rangeStart,
                DateTime rangeEnd,
                int bucketCount)
            {
                var refreshed = await ChartSeries.RefreshAsync(db, hostId, earlier, rangeStart, rangeEnd, bucketCount);
                var fresh = await ChartSeries.LoadAsync(db, hostId, rangeStart, rangeEnd, bucketCount);
                Assert.That(refreshed.Samples, Is.EqualTo(fresh.Samples), reason);
            }

            await AssertReadFromScratch("a resize lays a grid of its own", bucketed, start, end, 24);
            await AssertReadFromScratch(
                "a wider window on the same grid reaches back past the read", bucketed, start.AddSeconds(-300), end, 30);
            await AssertReadFromScratch(
                "an end that has gone backwards is a clock that did", bucketed, start.AddSeconds(-10), end.AddSeconds(-10), 20);
            await AssertReadFromScratch("attempts handed back as recorded are not buckets", recorded, start, end, 5000);
        }

        /// <summary>
        /// A window can slide on to where there are few enough attempts to draw as they were
        /// recorded, and then that is how they come back, as they would from scratch, rather than
        /// as the buckets the earlier read was carrying.
        /// </summary>
        [Test]
        public async Task RefreshAsync_HandsBackTheAttemptsAsRecordedOnceTheyFit()
        {
            const int bucketCount = 20;
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db, seconds: 700);
            var earlier = await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(100), BaseTime.AddSeconds(700), bucketCount);

            var carried = await ChartSeries.RefreshAsync(db, hostId, earlier, BaseTime.AddSeconds(690), BaseTime.AddSeconds(1290), bucketCount);
            var fresh = await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(690), BaseTime.AddSeconds(1290), bucketCount);

            Assert.Multiple(() =>
            {
                Assert.That(carried.Samples, Is.EqualTo(fresh.Samples));
                Assert.That(carried.BucketDuration, Is.Null, "the ten attempts left fit as they were recorded");
            });
        }

        /// <summary>
        /// An earlier read can be older than the range it is carried into, on the same grid, and
        /// then its newest bucket lies before the range starts, with whatever was recorded since
        /// lying in between. Reading on from that bucket would bring those in from outside the
        /// range; the read starts where the range does instead.
        /// </summary>
        [Test]
        public async Task RefreshAsync_StartsWhereTheRangeDoesOnceItHasSlidPastEverythingHeld()
        {
            const int bucketCount = 20;
            await using var db = new HostPingerDbContext(_options);
            var hostId = await SeedAsync(db, seconds: 700);
            var earlier = await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(100), BaseTime.AddSeconds(700), bucketCount);
            await RecordAsync(db, hostId, 700, 800);
            await RecordAsync(db, hostId, 1400, 1500);

            var carried = await ChartSeries.RefreshAsync(db, hostId, earlier, BaseTime.AddSeconds(900), BaseTime.AddSeconds(1500), bucketCount);
            var fresh = await ChartSeries.LoadAsync(db, hostId, BaseTime.AddSeconds(900), BaseTime.AddSeconds(1500), bucketCount);

            Assert.That(carried.Samples, Is.EqualTo(fresh.Samples));
        }

        /// <summary>
        /// One attempt a second for 1000 seconds, less a stretch with nothing recorded in it and a
        /// stretch that went unanswered — or for fewer seconds, for a test that goes on recording
        /// after reading.
        /// </summary>
        private static async Task<int> SeedAsync(HostPingerDbContext db, int seconds = 1000)
        {
            var host = new MonitoredHost { Name = "chart", Address = "chart.example", CreatedUtc = BaseTime };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            await RecordAsync(db, host.Id, 0, seconds);
            return host.Id;
        }

        /// <summary>
        /// The attempts of each second from <paramref name="fromSecond"/> up to
        /// <paramref name="toSecond"/>, on the pattern <see cref="SeedAsync"/> describes. Recorded in
        /// time order after whatever is there already, which is how rounds record them.
        /// </summary>
        private static async Task RecordAsync(HostPingerDbContext db, int hostId, int fromSecond, int toSecond)
        {
            db.PingAttempts.AddRange(Enumerable.Range(fromSecond, toSecond - fromSecond)
                .Where(i => i is < 300 or >= 400)
                .Select(i => new PingAttempt
                {
                    HostId = hostId,
                    TimestampUtc = BaseTime.AddSeconds(i),
                    RoundtripMs = i is >= 500 and < 600 ? null : i % 50,
                }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        /// <summary>Empties a stretch of attempts behind the back of a read already taken.</summary>
        private static Task<int> DeleteAsync(HostPingerDbContext db, int hostId, int fromSecond, int toSecond) =>
            db.PingAttempts
                .Where(a => a.HostId == hostId
                    && a.TimestampUtc >= BaseTime.AddSeconds(fromSecond)
                    && a.TimestampUtc < BaseTime.AddSeconds(toSecond))
                .ExecuteDeleteAsync();

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
