using HostPinger.Core.Data;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    /// <summary>
    /// The ways a connection can go through <see cref="DatabaseFileGate"/> that a restore's own tests
    /// do not reach: an open that fails, a wait given up, and EF's synchronous open. Each must leave
    /// the gate able to empty, or every restore after it would wait out its timeout and refuse.
    /// </summary>
    public class DatabaseFileGateTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private string _directory = null!;
        private DatabaseFileGate _gate = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public async Task SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _gate = new DatabaseFileGate();
            _options = OptionsFor(Path.Combine(_directory, "hostpinger.db"));
            await using var db = new HostPingerDbContext(_options);
            await db.Database.EnsureCreatedAsync();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        /// <summary>A connection that cannot be opened gives back the pass it was handed for it.</summary>
        [Test]
        public async Task AConnectionThatFailsToOpenGivesItsPassBack()
        {
            await using var db = new HostPingerDbContext(OptionsFor(Path.Combine(_directory, "missing", "nowhere.db")));

            Assert.That(async () => await db.Database.OpenConnectionAsync(), Throws.InstanceOf<SqliteException>());
            Assert.That(_gate.OpenConnections, Is.Zero);
        }

        /// <summary>
        /// A connection whose wait at the shut gate is given up takes no pass, so it has none to
        /// leave behind, and the next shut empties at once.
        /// </summary>
        [Test]
        public async Task AWaitGivenUpAtTheShutGateTakesNoPass()
        {
            var finish = new TaskCompletionSource();
            var shut = _gate.WhileShutAsync(Patience, () => finish.Task, CancellationToken.None);

            await using (var db = new HostPingerDbContext(_options))
            {
                using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                Assert.That(async () => await db.Database.OpenConnectionAsync(giveUp.Token),
                    Throws.InstanceOf<OperationCanceledException>());
            }

            finish.SetResult();
            await shut.WaitAsync(Patience);

            Assert.That(_gate.OpenConnections, Is.Zero);
            await _gate.WhileShutAsync(TimeSpan.FromMilliseconds(100), () => Task.CompletedTask, CancellationToken.None)
                .WaitAsync(Patience);
        }

        /// <summary>
        /// EF's synchronous open waits at the shut gate as its asynchronous one does, and goes
        /// through once the gate opens.
        /// </summary>
        [Test]
        public async Task ASynchronousOpenWaitsForTheGateToOpen()
        {
            var finish = new TaskCompletionSource();
            var shut = _gate.WhileShutAsync(Patience, () => finish.Task, CancellationToken.None);

            await using var db = new HostPingerDbContext(_options);
            var opening = Task.Run(() => db.Database.OpenConnection());
            await Task.WhenAny(opening, Task.Delay(TimeSpan.FromMilliseconds(500)));
            var openedWhileShut = opening.IsCompleted;

            finish.SetResult();
            await shut.WaitAsync(Patience);
            await opening.WaitAsync(Patience);

            Assert.Multiple(() =>
            {
                Assert.That(openedWhileShut, Is.False);
                Assert.That(_gate.OpenConnections, Is.EqualTo(1));
            });
        }

        private DbContextOptions<HostPingerDbContext> OptionsFor(string path) =>
            new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={path}")
                .AddInterceptors(_gate.Interceptor)
                .Options;
    }
}
