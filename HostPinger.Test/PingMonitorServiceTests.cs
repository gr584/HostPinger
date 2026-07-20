using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    public class PingMonitorServiceTests
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
        public async Task RunRound_RecordsRoundtripForUpHostsAndNullForDownHosts()
        {
            int upId, downId, pausedId;
            await using (var db = new HostPingerDbContext(_options))
            {
                var up = new MonitoredHost { Name = "up", Address = "up.example" };
                var down = new MonitoredHost { Name = "down", Address = "down.example" };
                var paused = new MonitoredHost { Name = "paused", Address = "paused.example", IsEnabled = false };
                db.Hosts.AddRange(up, down, paused);
                await db.SaveChangesAsync();
                (upId, downId, pausedId) = (up.Id, down.Id, paused.Id);
            }

            var sender = new FakePingSender { Results = { ["up.example"] = 42 } };
            var service = CreateService(sender);

            var recorded = await service.RunRoundAsync();

            Assert.That(recorded, Is.EqualTo(2));
            Assert.That(sender.Calls, Is.EquivalentTo(new[] { "up.example", "down.example" }));

            await using var verify = new HostPingerDbContext(_options);
            var attempts = await verify.PingAttempts.AsNoTracking().ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(attempts, Has.Count.EqualTo(2));
                Assert.That(attempts.Single(a => a.HostId == upId).RoundtripMs, Is.EqualTo(42));
                Assert.That(attempts.Single(a => a.HostId == downId).RoundtripMs, Is.Null);
                Assert.That(attempts.All(a => a.HostId != pausedId), "disabled hosts must not be pinged");
                Assert.That(attempts.All(a => Math.Abs((DateTime.UtcNow - a.TimestampUtc).TotalSeconds) < 30),
                    "timestamps should be current");
            });
        }

        [Test]
        public async Task RunRound_NoEnabledHosts_PingsNothing()
        {
            var sender = new FakePingSender();
            var service = CreateService(sender);

            var recorded = await service.RunRoundAsync();

            Assert.That(recorded, Is.Zero);
            Assert.That(sender.Calls, Is.Empty);
        }

        private PingMonitorService CreateService(IPingSender sender)
        {
            return new PingMonitorService(
                new TestDb.Factory(_options),
                sender,
                Microsoft.Extensions.Options.Options.Create(new PingerOptions()),
                new DatabasePruner(),
                NullLogger<PingMonitorService>.Instance);
        }

        private sealed class FakePingSender : IPingSender
        {
            public Dictionary<string, int?> Results { get; } = [];

            public List<string> Calls { get; } = [];

            public Task<int?> SendPingAsync(string address, int timeoutMilliseconds, CancellationToken cancellationToken = default)
            {
                lock (Calls)
                {
                    Calls.Add(address);
                }

                return Task.FromResult(Results.GetValueOrDefault(address));
            }
        }
    }
}
