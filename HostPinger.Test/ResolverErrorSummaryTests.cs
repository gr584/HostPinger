using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    /// <summary>
    /// Covers what the resolver errors page reads: one row per address that has ever failed to
    /// resolve, the newest failure first, with the two windows counted from the moment it is asked
    /// rather than from any calendar boundary.
    /// </summary>
    public class ResolverErrorSummaryTests
    {
        private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

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
        public async Task Load_NothingHasFailed_IsEmpty()
        {
            await using var db = new HostPingerDbContext(_options);

            Assert.That(await ResolverErrorSummary.LoadAsync(db, Now), Is.Empty);
        }

        /// <summary>
        /// Every failure of one address folds into a single row, and the reason shown is the one
        /// from its most recent failure — the state it is in now, not the one it started in.
        /// </summary>
        [Test]
        public async Task Load_FoldsEveryFailureOfAnAddressIntoOneRowCarryingItsLatestReason()
        {
            await using var db = new HostPingerDbContext(_options);
            await SeedAsync(
                db,
                ("slow.example", Now.AddMinutes(-30), ResolverFailure.LookupFailed),
                ("slow.example", Now.AddMinutes(-20), ResolverFailure.NoAddresses),
                ("slow.example", Now.AddMinutes(-10), ResolverFailure.TimedOut));

            var rows = await ResolverErrorSummary.LoadAsync(db, Now);

            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(rows[0].Address, Is.EqualTo("slow.example"));
                Assert.That(rows[0].LastUtc, Is.EqualTo(Now.AddMinutes(-10)));
                Assert.That(rows[0].LastReason, Is.EqualTo(ResolverFailure.TimedOut));
                Assert.That(rows[0].Last24Hours, Is.EqualTo(3));
                Assert.That(rows[0].Last30Days, Is.EqualTo(3));
            });
        }

        /// <summary>
        /// The counts are what separate an address that is failing now from one that stopped, so
        /// each window takes in exactly its own stretch of the past — and an address whose failures
        /// have all aged out of the narrow ones is still listed, with the noughts that say so.
        /// </summary>
        [Test]
        public async Task Load_CountsOnlyTheFailuresInsideEachWindow()
        {
            await using var db = new HostPingerDbContext(_options);
            await SeedAsync(
                db,
                ("mixed.example", Now.AddHours(-1), ResolverFailure.TimedOut),
                ("mixed.example", Now.AddHours(-23), ResolverFailure.TimedOut),
                ("mixed.example", Now.AddHours(-25), ResolverFailure.TimedOut),
                ("mixed.example", Now.AddDays(-6), ResolverFailure.TimedOut),
                ("mixed.example", Now.AddDays(-8), ResolverFailure.TimedOut),
                ("mixed.example", Now.AddDays(-29), ResolverFailure.TimedOut),
                ("stopped.example", Now.AddDays(-14), ResolverFailure.LookupFailed));

            var rows = await ResolverErrorSummary.LoadAsync(db, Now);
            var mixed = rows.Single(r => r.Address == "mixed.example");
            var stopped = rows.Single(r => r.Address == "stopped.example");

            Assert.Multiple(() =>
            {
                Assert.That(mixed.Last24Hours, Is.EqualTo(2));
                Assert.That(mixed.Last7Days, Is.EqualTo(4));
                Assert.That(mixed.Last30Days, Is.EqualTo(6));
                Assert.That(stopped.Last24Hours, Is.Zero);
                Assert.That(stopped.Last7Days, Is.Zero);
                Assert.That(stopped.Last30Days, Is.EqualTo(1));
                Assert.That(stopped.LastUtc, Is.EqualTo(Now.AddDays(-14)),
                    "an address that stopped failing is still worth listing, with when it last did");
            });
        }

        /// <summary>
        /// The widest window is the retention, so nothing the pruner has left behind falls outside
        /// it — but a row can outlive the window by up to the round that removes it, and the column
        /// counts the window it names rather than the group it is taken from.
        /// </summary>
        [Test]
        public async Task Load_LeavesAFailureOlderThanTheRetentionOutOfTheThirtyDayCount()
        {
            await using var db = new HostPingerDbContext(_options);
            await SeedAsync(
                db,
                ("lingering.example", Now - ResolverError.Retention - TimeSpan.FromMinutes(1), ResolverFailure.TimedOut),
                ("lingering.example", Now.AddMinutes(-1), ResolverFailure.TimedOut));

            var rows = await ResolverErrorSummary.LoadAsync(db, Now);

            Assert.That(rows.Single().Last30Days, Is.EqualTo(1));
        }

        /// <summary>
        /// Most recent first, which is what the page is ordered by. Addresses that failed in the
        /// same round are ordered by address so a refresh does not shuffle them.
        /// </summary>
        [Test]
        public async Task Load_OrdersByTheMostRecentFailure()
        {
            var round = Now.AddMinutes(-5);
            await using var db = new HostPingerDbContext(_options);
            await SeedAsync(
                db,
                ("yesterday.example", Now.AddDays(-1), ResolverFailure.TimedOut),
                ("beta.example", round, ResolverFailure.TimedOut),
                ("alpha.example", round, ResolverFailure.TimedOut),
                ("hour.example", Now.AddHours(-1), ResolverFailure.TimedOut));

            var rows = await ResolverErrorSummary.LoadAsync(db, Now);

            Assert.That(
                rows.Select(r => r.Address),
                Is.EqualTo(new[] { "alpha.example", "beta.example", "hour.example", "yesterday.example" }));
        }

        /// <summary>
        /// The errors belong to the address rather than to a host row, so the host's name is looked
        /// up as it stands now — and an address no host carries any more still lists, which is the
        /// case that would be lost entirely if these hung off the host and cascaded with it.
        /// </summary>
        [Test]
        public async Task Load_NamesTheHostCarryingTheAddressWhenOneStillDoes()
        {
            await using var db = new HostPingerDbContext(_options);
            db.Hosts.Add(new MonitoredHost { Name = "Database server", Address = "db.example" });
            await db.SaveChangesAsync();
            await SeedAsync(
                db,
                ("db.example", Now.AddMinutes(-1), ResolverFailure.TimedOut),
                ("deleted.example", Now.AddMinutes(-2), ResolverFailure.LookupFailed));

            var rows = await ResolverErrorSummary.LoadAsync(db, Now);

            Assert.Multiple(() =>
            {
                Assert.That(rows.Single(r => r.Address == "db.example").HostName, Is.EqualTo("Database server"));
                Assert.That(rows.Single(r => r.Address == "deleted.example").HostName, Is.Null);
            });
        }

        /// <summary>
        /// Rows are seeded in timestamp order, as the rounds that record them do: the latest reason
        /// is read off the largest id in each group, which is only the newest row while that holds.
        /// </summary>
        private static async Task SeedAsync(
            HostPingerDbContext db,
            params (string Address, DateTime TimestampUtc, ResolverFailure Reason)[] errors)
        {
            db.ResolverErrors.AddRange(errors
                .OrderBy(e => e.TimestampUtc)
                .Select(e => new ResolverError
                {
                    Address = e.Address,
                    TimestampUtc = e.TimestampUtc,
                    Reason = e.Reason,
                }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
    }
}
