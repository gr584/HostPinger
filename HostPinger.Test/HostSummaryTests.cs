using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class HostSummaryTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public void SetUp()
        {
            (_connection, _options) = TestDb.CreateInMemory();
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
        }

        [Test]
        public async Task LoadAsync_OrdersHostsByName()
        {
            await using var db = new HostPingerDbContext(_options);
            db.Hosts.AddRange(
                new MonitoredHost { Name = "charlie", Address = "c.example" },
                new MonitoredHost { Name = "alpha", Address = "a.example" },
                new MonitoredHost { Name = "bravo", Address = "b.example" });
            await db.SaveChangesAsync();

            var summaries = await HostSummary.LoadAsync(db);

            Assert.That(summaries.Select(s => s.Host.Name), Is.EqualTo(new[] { "alpha", "bravo", "charlie" }));
        }

        [Test]
        public async Task LoadAsync_ReturnsTheMostRecentAttemptPerHost()
        {
            var start = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var first = new MonitoredHost { Name = "first", Address = "first.example" };
            var second = new MonitoredHost { Name = "second", Address = "second.example" };
            db.Hosts.AddRange(first, second);
            await db.SaveChangesAsync();
            db.PingAttempts.AddRange(
                new PingAttempt { HostId = first.Id, TimestampUtc = start, RoundtripMs = 10 },
                // Inserted out of order so the result cannot come from insertion order alone.
                new PingAttempt { HostId = first.Id, TimestampUtc = start.AddMinutes(2), RoundtripMs = 30 },
                new PingAttempt { HostId = first.Id, TimestampUtc = start.AddMinutes(1), RoundtripMs = 20 },
                new PingAttempt { HostId = second.Id, TimestampUtc = start, RoundtripMs = 99 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var summaries = await HostSummary.LoadAsync(db);

            Assert.Multiple(() =>
            {
                Assert.That(summaries[0].Last?.TimestampUtc, Is.EqualTo(start.AddMinutes(2)));
                Assert.That(summaries[0].Last?.RoundtripMs, Is.EqualTo(30));
                Assert.That(summaries[1].Last?.RoundtripMs, Is.EqualTo(99));
            });
        }

        [Test]
        public async Task LoadAsync_LeavesLastNullForAHostThatWasNeverPinged()
        {
            await using var db = new HostPingerDbContext(_options);
            db.Hosts.Add(new MonitoredHost { Name = "fresh", Address = "fresh.example" });
            await db.SaveChangesAsync();

            var summaries = await HostSummary.LoadAsync(db);

            Assert.That(summaries.Single().Last, Is.Null);
        }

        /// <summary>
        /// A host that answered nothing must still report an attempt — the page tells "Down" from
        /// "Waiting…" by whether <see cref="HostSummary.Last"/> exists, so folding an unanswered
        /// ping into null would mislabel a host that is down as one that has never been pinged.
        /// </summary>
        [Test]
        public async Task LoadAsync_ReportsAnUnansweredPingAsAnAttemptWithoutARoundtrip()
        {
            var timestamp = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);
            await using var db = new HostPingerDbContext(_options);
            var host = new MonitoredHost { Name = "down", Address = "down.example" };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = timestamp, RoundtripMs = null });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var summaries = await HostSummary.LoadAsync(db);

            Assert.Multiple(() =>
            {
                Assert.That(summaries.Single().Last, Is.Not.Null);
                Assert.That(summaries.Single().Last!.TimestampUtc, Is.EqualTo(timestamp));
                Assert.That(summaries.Single().Last!.RoundtripMs, Is.Null);
            });
        }

        /// <summary>
        /// Guards the fix for the slow Hosts page. Projecting the last attempt as an entity makes EF
        /// emit ROW_NUMBER() OVER (PARTITION BY ...), which scans the whole PingAttempts table; the
        /// results stay correct, so only the shape of the SQL catches the regression.
        /// </summary>
        [Test]
        public void BuildQuery_DoesNotWindowOverTheWholeAttemptsTable()
        {
            using var db = new HostPingerDbContext(_options);

            var sql = HostSummary.BuildQuery(db).ToQueryString();

            Assert.Multiple(() =>
            {
                Assert.That(sql, Does.Not.Contain("ROW_NUMBER").IgnoreCase,
                    "The last attempt must be read with an indexed LIMIT 1, not a window over every attempt.");
                Assert.That(sql, Does.Contain("LIMIT 1"));
            });
        }

        /// <summary>
        /// The correlated subqueries are only fast because SQLite can walk
        /// IX_PingAttempts_HostId_TimestampUtc backwards; without the index it scans instead.
        /// </summary>
        [Test]
        public async Task BuildQuery_SeeksTheHostTimestampIndex()
        {
            using var db = new HostPingerDbContext(_options);
            var sql = HostSummary.BuildQuery(db).ToQueryString();

            await using var command = _connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(3));
                }
            }

            var detail = string.Join(" | ", plan);
            Assert.Multiple(() =>
            {
                Assert.That(detail, Does.Contain("IX_PingAttempts_HostId_TimestampUtc"),
                    $"Plan should seek the host/timestamp index. Plan was: {detail}");
                Assert.That(detail, Does.Not.Contain("SCAN PingAttempts"),
                    $"Plan should not scan the attempts table. Plan was: {detail}");
            });
        }
    }
}
