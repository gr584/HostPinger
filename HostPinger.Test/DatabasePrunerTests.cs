using HostPinger.Core.Data;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class DatabasePrunerTests
    {
        private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private string _dbPath = string.Empty;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public async Task SetUp()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"hostpinger-test-{Guid.NewGuid():N}.db");
            _options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options;

            await using var db = new HostPingerDbContext(_options);
            await HostPingerDatabase.EnableIncrementalVacuumAsync(db);
            await db.Database.EnsureCreatedAsync();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_dbPath);
        }

        [Test]
        public async Task EnforceSizeLimit_ShrinksFileAndKeepsNewestRows()
        {
            const long limitBytes = 150 * 1024;
            await using var db = new HostPingerDbContext(_options);
            await SeedAttemptsAsync(db, 20_000);

            var sizeBefore = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
            Assert.That(sizeBefore, Is.GreaterThan(limitBytes), "seed data should exceed the limit");

            var deleted = await new DatabasePruner().EnforceSizeLimitAsync(db, limitBytes);

            Assert.That(deleted, Is.GreaterThan(0));
            Assert.That(await DatabasePruner.GetDatabaseSizeBytesAsync(db), Is.LessThanOrEqualTo(limitBytes));

            var remaining = await db.PingAttempts.AsNoTracking().ToListAsync();
            Assert.That(remaining, Is.Not.Empty, "pruning must not wipe the table");
            Assert.That(remaining.Min(a => a.TimestampUtc), Is.GreaterThan(BaseTime),
                "the oldest rows should be the ones deleted");
            Assert.That(remaining.Max(a => a.TimestampUtc), Is.EqualTo(BaseTime.AddSeconds(19_999)),
                "the newest row must survive");
        }

        [Test]
        public async Task EnforceSizeLimit_NonPositiveLimitDisablesPruning()
        {
            await using var db = new HostPingerDbContext(_options);
            await SeedAttemptsAsync(db, 1_000);

            var deleted = await new DatabasePruner().EnforceSizeLimitAsync(db, 0);

            Assert.That(deleted, Is.Zero);
            Assert.That(await db.PingAttempts.CountAsync(), Is.EqualTo(1_000));
        }

        private static async Task SeedAttemptsAsync(HostPingerDbContext db, int count)
        {
            var host = new MonitoredHost { Name = "test", Address = "test.example", CreatedUtc = BaseTime };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();

            for (var offset = 0; offset < count; offset += 5_000)
            {
                var batch = Enumerable.Range(offset, Math.Min(5_000, count - offset))
                    .Select(i => new PingAttempt
                    {
                        HostId = host.Id,
                        TimestampUtc = BaseTime.AddSeconds(i),
                        RoundtripMs = i % 100,
                    });
                db.PingAttempts.AddRange(batch);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }
    }
}
