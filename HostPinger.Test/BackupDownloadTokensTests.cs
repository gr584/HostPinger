using HostPinger.Security;

namespace HostPinger.Test
{
    /// <summary>
    /// The tokens that hand a snapshot an unlocked page has written across to the backup download,
    /// which is a plain GET and cannot see the circuit. Each is good once, for two minutes, and for
    /// nothing it was not issued as; and the snapshot of one that is never spent does not outlive it.
    /// </summary>
    public class BackupDownloadTokensTests
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        private string _directory = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        /// <summary>Spending a token hands over its snapshot, untouched; spending it again gets nothing.</summary>
        [Test]
        public void TryConsume_HandsOverTheSnapshotOnce()
        {
            var tokens = new BackupDownloadTokens(new Clock(Start));
            var snapshot = Snapshot();
            var token = tokens.Issue(snapshot);

            var spent = tokens.TryConsume(token, out var handedOver);
            var again = tokens.TryConsume(token, out var second);

            Assert.Multiple(() =>
            {
                Assert.That(spent, Is.True);
                Assert.That(handedOver, Is.EqualTo(snapshot));
                Assert.That(File.Exists(snapshot), "the endpoint's to stream now");
                Assert.That(again, Is.False, "spent");
                Assert.That(second, Is.Null);
            });
        }

        /// <summary>A token in a pasted address or a proxy log is stale by the time anyone reads it there.</summary>
        [Test]
        public void TryConsume_RefusesATokenPastItsTwoMinutes()
        {
            var clock = new Clock(Start);
            var tokens = new BackupDownloadTokens(clock);
            var fresh = tokens.Issue(Snapshot());
            var stale = tokens.Issue(Snapshot());

            clock.Now = Start.AddMinutes(2).AddTicks(-1);
            var justInTime = tokens.TryConsume(fresh, out _);
            clock.Now = Start.AddMinutes(2);

            Assert.Multiple(() =>
            {
                Assert.That(justInTime, Is.True);
                Assert.That(tokens.TryConsume(stale, out _), Is.False);
            });
        }

        /// <summary>
        /// Refusing a stale token spends it too, since a token that has been seen is done, and
        /// deletes its snapshot, which nobody will be handed now.
        /// </summary>
        [Test]
        public void TryConsume_SpendsAStaleTokenAndDeletesItsSnapshot()
        {
            var clock = new Clock(Start);
            var tokens = new BackupDownloadTokens(clock);
            var snapshot = Snapshot();
            var token = tokens.Issue(snapshot);

            clock.Now = Start.AddMinutes(5);
            tokens.TryConsume(token, out _);
            clock.Now = Start;

            Assert.Multiple(() =>
            {
                Assert.That(tokens.TryConsume(token, out _), Is.False);
                Assert.That(File.Exists(snapshot), Is.False);
            });
        }

        /// <summary>Only what was issued: a made-up token, or one from another instance, is refused.</summary>
        [Test]
        public void TryConsume_RefusesWhatItNeverIssued()
        {
            var tokens = new BackupDownloadTokens(new Clock(Start));
            var others = new BackupDownloadTokens(new Clock(Start));

            Assert.Multiple(() =>
            {
                Assert.That(tokens.TryConsume(others.Issue(Snapshot()), out _), Is.False);
                Assert.That(tokens.TryConsume(string.Empty, out _), Is.False);
            });
        }

        /// <summary>Unguessable, and different every time: 32 random bytes, written as hex.</summary>
        [Test]
        public void Issue_HandsOutLongTokensThatDoNotRepeat()
        {
            var tokens = new BackupDownloadTokens(new Clock(Start));
            var snapshot = Snapshot();

            var issued = Enumerable.Range(0, 100).Select(_ => tokens.Issue(snapshot)).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(issued, Is.Unique);
                Assert.That(issued, Has.All.Matches<string>(t => t is { Length: 64 } && t.All(Uri.IsHexDigit)));
            });
        }

        /// <summary>
        /// A token nobody spends goes when its two minutes are up, and its snapshot with it: the
        /// browser never asked for the download, and a copy of the database would otherwise sit
        /// beside it until the next start. One that was spent leaves its snapshot alone.
        /// </summary>
        [Test]
        public void Expiry_DeletesTheSnapshotOfATokenNobodySpent()
        {
            var clock = new Clock(Start);
            var tokens = new BackupDownloadTokens(clock);
            var forgotten = Snapshot();
            var downloaded = Snapshot();
            var forgottenToken = tokens.Issue(forgotten);
            tokens.TryConsume(tokens.Issue(downloaded), out _);

            clock.Advance(TimeSpan.FromMinutes(2).Subtract(TimeSpan.FromTicks(1)));
            var beforeItsTime = File.Exists(forgotten);
            clock.Advance(TimeSpan.FromTicks(1));

            Assert.Multiple(() =>
            {
                Assert.That(beforeItsTime, Is.True);
                Assert.That(File.Exists(forgotten), Is.False);
                Assert.That(File.Exists(downloaded), Is.True);
                Assert.That(tokens.TryConsume(forgottenToken, out _), Is.False);
            });
        }

        private string Snapshot()
        {
            var path = Path.Combine(_directory, $"hostpinger-backup-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, "snapshot");
            return path;
        }

        /// <summary>
        /// A clock the test moves by hand, with timers that fire when it is moved past them rather
        /// than in real time.
        /// </summary>
        private sealed class Clock(DateTimeOffset now) : TimeProvider
        {
            private readonly List<FakeTimer> _timers = [];

            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = new FakeTimer(callback, state, Now + dueTime);
                _timers.Add(timer);
                return timer;
            }

            public void Advance(TimeSpan by)
            {
                Now += by;
                foreach (var timer in _timers.Where(t => !t.IsDisposed && t.DueAt <= Now).ToList())
                {
                    timer.Fire();
                }
            }

            private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
            {
                public DateTimeOffset DueAt { get; } = dueAt;

                public bool IsDisposed { get; private set; }

                public void Fire()
                {
                    IsDisposed = true;
                    callback(state);
                }

                public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

                public void Dispose() => IsDisposed = true;

                public ValueTask DisposeAsync()
                {
                    Dispose();
                    return ValueTask.CompletedTask;
                }
            }
        }
    }
}
