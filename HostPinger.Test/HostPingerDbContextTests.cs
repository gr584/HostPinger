using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class HostPingerDbContextTests
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
        public async Task DuplicateAddress_IsRejected()
        {
            await using var db = new HostPingerDbContext(_options);
            db.Hosts.Add(new MonitoredHost { Name = "one", Address = "dup.example" });
            await db.SaveChangesAsync();

            db.Hosts.Add(new MonitoredHost { Name = "two", Address = "dup.example" });

            Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Test]
        public async Task DeletingHost_CascadesToItsPingAttempts()
        {
            await using var db = new HostPingerDbContext(_options);
            var host = new MonitoredHost { Name = "h", Address = "h.example" };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            db.PingAttempts.AddRange(
                new PingAttempt { HostId = host.Id, TimestampUtc = DateTime.UtcNow, RoundtripMs = 1 },
                new PingAttempt { HostId = host.Id, TimestampUtc = DateTime.UtcNow, RoundtripMs = null });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Hosts.Where(h => h.Id == host.Id).ExecuteDeleteAsync();

            Assert.That(await db.PingAttempts.CountAsync(), Is.Zero);
        }
    }
}
