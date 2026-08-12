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

        /// <summary>
        /// The limit has to hold whichever table is filling the file. An address that never resolves
        /// records an error every round for as long as it stays configured, so a pruner that deleted
        /// only attempts would be held above the limit by the errors, with nothing left to delete
        /// and no way back under it.
        /// </summary>
        [Test]
        public async Task EnforceSizeLimit_PrunesResolverErrorsAsWell()
        {
            // Roomier than the limit the attempts are pruned to above, because an error row carries
            // an address and this many of them are several times the size. A limit that only a few
            // hundred rows fit under would be reached by a batch that emptied the table, and this
            // test is as much about what survives as about what goes.
            const long limitBytes = 512 * 1024;
            await using var db = new HostPingerDbContext(_options);
            await SeedResolverErrorsAsync(db, 20_000);

            var sizeBefore = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
            Assert.That(sizeBefore, Is.GreaterThan(limitBytes), "seed data should exceed the limit");

            var deleted = await new DatabasePruner().EnforceSizeLimitAsync(db, limitBytes);

            Assert.That(deleted, Is.GreaterThan(0));
            Assert.That(await DatabasePruner.GetDatabaseSizeBytesAsync(db), Is.LessThanOrEqualTo(limitBytes));

            var remaining = await db.ResolverErrors.AsNoTracking().ToListAsync();
            Assert.That(remaining, Is.Not.Empty, "pruning must not wipe the table");
            Assert.That(remaining.Min(e => e.TimestampUtc), Is.GreaterThan(BaseTime),
                "the oldest rows should be the ones deleted");
            Assert.That(remaining.Max(e => e.TimestampUtc), Is.EqualTo(BaseTime.AddSeconds(19_999)),
                "the newest row must survive");
        }

        /// <summary>
        /// Both tables are trimmed on every pass rather than one being emptied before the other is
        /// touched, so neither history is thrown away to spare the other.
        /// </summary>
        [Test]
        public async Task EnforceSizeLimit_TrimsBothHistoriesRatherThanEmptyingOne()
        {
            const long limitBytes = 512 * 1024;
            await using var db = new HostPingerDbContext(_options);
            await SeedAttemptsAsync(db, 10_000);
            await SeedResolverErrorsAsync(db, 10_000);

            await new DatabasePruner().EnforceSizeLimitAsync(db, limitBytes);

            Assert.Multiple(async () =>
            {
                Assert.That(await db.PingAttempts.CountAsync(), Is.GreaterThan(0).And.LessThan(10_000));
                Assert.That(await db.ResolverErrors.CountAsync(), Is.GreaterThan(0).And.LessThan(10_000));
            });
        }

        /// <summary>
        /// Nothing reads past the retention, so nothing is kept past it. The boundary is exact
        /// because the widest column on the page counts the same window: a row a minute the wrong
        /// side of it would be one the page could never show.
        /// </summary>
        [Test]
        public async Task EnforceResolverErrorRetention_DeletesOnlyWhatHasAgedPastIt()
        {
            var now = BaseTime.AddDays(60);
            await using var db = new HostPingerDbContext(_options);
            db.ResolverErrors.AddRange(
                new ResolverError { Address = "old.example", TimestampUtc = now - ResolverError.Retention - TimeSpan.FromMinutes(1), Reason = ResolverFailure.TimedOut },
                new ResolverError { Address = "edge.example", TimestampUtc = now - ResolverError.Retention, Reason = ResolverFailure.TimedOut },
                new ResolverError { Address = "recent.example", TimestampUtc = now.AddDays(-1), Reason = ResolverFailure.LookupFailed });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var deleted = await new DatabasePruner().EnforceResolverErrorRetentionAsync(db, now);

            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(
                await db.ResolverErrors.AsNoTracking().Select(e => e.Address).ToListAsync(),
                Is.EquivalentTo(new[] { "edge.example", "recent.example" }));
        }

        /// <summary>
        /// The retention is about what can still be read rather than about disk, so it applies
        /// whatever the size limit is — including the zero that turns size pruning off — and it
        /// leaves the ping history alone, which has its own pages to answer to and no such window.
        /// </summary>
        [Test]
        public async Task EnforceResolverErrorRetention_LeavesThePingHistoryAlone()
        {
            var now = BaseTime.AddDays(60);
            await using var db = new HostPingerDbContext(_options);
            await SeedAttemptsAsync(db, 100);
            db.ResolverErrors.Add(new ResolverError
            {
                Address = "old.example",
                TimestampUtc = BaseTime,
                Reason = ResolverFailure.TimedOut,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await new DatabasePruner().EnforceResolverErrorRetentionAsync(db, now);

            Assert.Multiple(async () =>
            {
                Assert.That(await db.ResolverErrors.CountAsync(), Is.Zero);
                Assert.That(await db.PingAttempts.CountAsync(), Is.EqualTo(100),
                    "attempts far older than the resolver retention are not its business");
            });
        }

        [Test]
        public async Task EnforceSizeLimit_NonPositiveLimitDisablesPruning()
        {
            await using var db = new HostPingerDbContext(_options);
            await SeedAttemptsAsync(db, 1_000);
            await SeedResolverErrorsAsync(db, 1_000);

            var deleted = await new DatabasePruner().EnforceSizeLimitAsync(db, 0);

            Assert.That(deleted, Is.Zero);
            Assert.Multiple(async () =>
            {
                Assert.That(await db.PingAttempts.CountAsync(), Is.EqualTo(1_000));
                Assert.That(await db.ResolverErrors.CountAsync(), Is.EqualTo(1_000));
            });
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

        /// <summary>One failure a second from <see cref="BaseTime"/>, as a name that never resolves records them.</summary>
        private static async Task SeedResolverErrorsAsync(HostPingerDbContext db, int count)
        {
            for (var offset = 0; offset < count; offset += 5_000)
            {
                var batch = Enumerable.Range(offset, Math.Min(5_000, count - offset))
                    .Select(i => new ResolverError
                    {
                        Address = "gone.example",
                        TimestampUtc = BaseTime.AddSeconds(i),
                        Reason = ResolverFailure.LookupFailed,
                    });
                db.ResolverErrors.AddRange(batch);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }
    }
}
