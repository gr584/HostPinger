using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    /// <summary>
    /// What one read of the graph is made of: which hosts it lists, which it reads a series for,
    /// and which series it carries on rather than reads afresh. How a series itself is read is
    /// settled in ChartSeriesTests; this is the composition the graph page used to do inline.
    /// </summary>
    public class GraphReadTests
    {
        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime End = Start.AddSeconds(1000);

        private const int Points = 20;

        private string _dbPath = string.Empty;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public async Task SetUp()
        {
            // A file, because ChartSeries opens and closes the connection around its aggregate.
            _dbPath = Path.Combine(Path.GetTempPath(), $"hostpinger-graph-{Guid.NewGuid():N}.db");
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
        /// Every host is listed, plotted or not, in the order they were added rather than by name:
        /// the order is what each host's colour comes from, and a name order would repaint every
        /// host after one added or renamed.
        /// </summary>
        [Test]
        public async Task LoadAsync_ListsEveryHostInTheOrderTheyWereAdded()
        {
            var zulu = await AddHostAsync("zulu");
            await AddHostAsync("alpha");
            await AddHostAsync("mike");

            var read = await LoadAsync(plotted: [zulu]);

            Assert.That(read.Hosts.Select(h => h.Name), Is.EqualTo(new[] { "zulu", "alpha", "mike" }));
        }

        /// <summary>
        /// A series is read for each plotted host and for no other, and a plotted id no host has
        /// any more — a host deleted while its graph is open — is simply left out. Each series is
        /// the one ChartSeries reads for that host over the range.
        /// </summary>
        [Test]
        public async Task LoadAsync_ReadsASeriesForEachPlottedHostThatExists()
        {
            var zulu = await AddHostAsync("zulu");
            var alpha = await AddHostAsync("alpha");
            var mike = await AddHostAsync("mike");

            var read = await LoadAsync(plotted: [zulu, mike, mike + 100]);

            await using var db = new HostPingerDbContext(_options);
            Assert.Multiple(async () =>
            {
                Assert.That(read.SeriesReads.Keys, Is.EquivalentTo(new[] { zulu, mike }));
                Assert.That(read.SeriesReads.ContainsKey(alpha), Is.False);
                Assert.That(read.SeriesReads[zulu].Samples, Is.EqualTo((await ChartSeries.LoadAsync(db, zulu, Start, End, Points)).Samples));
                Assert.That(read.SeriesReads[mike].Samples, Is.EqualTo((await ChartSeries.LoadAsync(db, mike, Start, End, Points)).Samples));
                Assert.That(read.SeriesReads[zulu].Samples, Is.Not.EqualTo(read.SeriesReads[mike].Samples), "the hosts were seeded apart");
            });
        }

        /// <summary>The read says what range and density it was taken over, which the chart draws its axis from.</summary>
        [Test]
        public async Task LoadAsync_HandsBackTheRangeAndDensityItReadOver()
        {
            var zulu = await AddHostAsync("zulu");

            var read = await LoadAsync(plotted: [zulu]);

            Assert.Multiple(() =>
            {
                Assert.That(read.RangeStartUtc, Is.EqualTo(Start));
                Assert.That(read.RangeEndUtc, Is.EqualTo(End));
                Assert.That(read.Points, Is.EqualTo(Points));
            });
        }

        /// <summary>
        /// A host the earlier read holds a series for is carried on from it, and any other plotted
        /// host is read from scratch. Told apart by changing rows behind the earlier read's newest
        /// point, which a carried-on series does not see and a fresh one does.
        /// </summary>
        [Test]
        public async Task LoadAsync_CarriesOnTheSeriesItHoldsAndReadsTheRestFromScratch()
        {
            var held = await AddHostAsync("held");
            var added = await AddHostAsync("added");
            var earlier = await LoadAsync(plotted: [held]);
            await using (var db = new HostPingerDbContext(_options))
            {
                await db.PingAttempts.Where(a => a.TimestampUtc < Start.AddSeconds(200)).ExecuteDeleteAsync();
            }

            var read = await LoadAsync(plotted: [held, added], earlier.SeriesReads);

            await using var check = new HostPingerDbContext(_options);
            var heldNow = (await ChartSeries.LoadAsync(check, held, Start, End, Points)).Samples;
            Assert.Multiple(async () =>
            {
                Assert.That(read.SeriesReads[held].Samples, Is.EqualTo(earlier.SeriesReads[held].Samples), "carried on");
                Assert.That(heldNow, Is.Not.EqualTo(earlier.SeriesReads[held].Samples), "the change shows in a fresh read");
                Assert.That(read.SeriesReads[added].Samples, Is.EqualTo((await ChartSeries.LoadAsync(check, added, Start, End, Points)).Samples), "read from scratch");
            });
        }

        /// <summary>
        /// The refresh that follows unticking a host in the picker: the earlier read still holds a
        /// series for it, and it is dropped rather than carried on, while the host still plotted is.
        /// </summary>
        [Test]
        public async Task LoadAsync_LeavesOutAHostTheEarlierReadHeldThatIsNoLongerPlotted()
        {
            var kept = await AddHostAsync("kept");
            var unticked = await AddHostAsync("unticked");
            var earlier = await LoadAsync(plotted: [kept, unticked]);

            var read = await LoadAsync(plotted: [kept], earlier.SeriesReads);

            Assert.Multiple(() =>
            {
                Assert.That(read.SeriesReads.Keys, Is.EquivalentTo(new[] { kept }));
                Assert.That(read.SeriesReads[kept].Samples, Is.EqualTo(earlier.SeriesReads[kept].Samples));
            });
        }

        private async Task<GraphRead> LoadAsync(
            int[] plotted,
            IReadOnlyDictionary<int, ChartSeriesRead>? earlier = null)
        {
            await using var db = new HostPingerDbContext(_options);
            return await GraphRead.LoadAsync(db, plotted.ToHashSet(), earlier, Start, End, Points);
        }

        /// <summary>
        /// A host with an attempt every ten seconds across the range, answered in a round trip that
        /// differs from host to host, and unanswered for one stretch so its series is not flat.
        /// </summary>
        private async Task<int> AddHostAsync(string name)
        {
            await using var db = new HostPingerDbContext(_options);
            var host = new MonitoredHost { Name = name, Address = $"{name}.example", CreatedUtc = Start };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            var roundtrip = 10 + host.Id * 13;
            db.PingAttempts.AddRange(Enumerable.Range(0, 100).Select(i => new PingAttempt
            {
                HostId = host.Id,
                TimestampUtc = Start.AddSeconds(i * 10),
                RoundtripMs = i is >= 40 and < 50 ? null : roundtrip + i % 5,
            }));
            await db.SaveChangesAsync();
            return host.Id;
        }
    }
}
