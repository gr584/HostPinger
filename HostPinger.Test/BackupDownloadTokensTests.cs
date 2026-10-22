using HostPinger.Security;

namespace HostPinger.Test
{
    /// <summary>
    /// The tokens that carry an unlocked page's say-so across to the backup download, which is a
    /// plain GET and cannot see the circuit. Each is good once, for two minutes, and for nothing it
    /// was not issued as.
    /// </summary>
    public class BackupDownloadTokensTests
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        [Test]
        public void TryConsume_AcceptsATokenItIssuedOnce()
        {
            var tokens = new BackupDownloadTokens(new Clock(Start));
            var token = tokens.Issue();

            Assert.Multiple(() =>
            {
                Assert.That(tokens.TryConsume(token), Is.True);
                Assert.That(tokens.TryConsume(token), Is.False, "spent");
            });
        }

        /// <summary>A token in a pasted address or a proxy log is stale by the time anyone reads it there.</summary>
        [Test]
        public void TryConsume_RefusesATokenPastItsTwoMinutes()
        {
            var clock = new Clock(Start);
            var tokens = new BackupDownloadTokens(clock);
            var fresh = tokens.Issue();
            var stale = tokens.Issue();

            clock.Now = Start.AddMinutes(2).AddTicks(-1);
            var justInTime = tokens.TryConsume(fresh);
            clock.Now = Start.AddMinutes(2);

            Assert.Multiple(() =>
            {
                Assert.That(justInTime, Is.True);
                Assert.That(tokens.TryConsume(stale), Is.False);
            });
        }

        /// <summary>Refusing a stale token spends it too: a token that has been seen is done.</summary>
        [Test]
        public void TryConsume_SpendsAStaleTokenAsItRefusesIt()
        {
            var clock = new Clock(Start);
            var tokens = new BackupDownloadTokens(clock);
            var token = tokens.Issue();

            clock.Now = Start.AddMinutes(5);
            tokens.TryConsume(token);
            clock.Now = Start;

            Assert.That(tokens.TryConsume(token), Is.False);
        }

        /// <summary>Only what was issued: a made-up token, or one from another instance, is refused.</summary>
        [Test]
        public void TryConsume_RefusesWhatItNeverIssued()
        {
            var tokens = new BackupDownloadTokens(new Clock(Start));
            var others = new BackupDownloadTokens(new Clock(Start));

            Assert.Multiple(() =>
            {
                Assert.That(tokens.TryConsume(others.Issue()), Is.False);
                Assert.That(tokens.TryConsume(string.Empty), Is.False);
            });
        }

        /// <summary>Unguessable, and different every time: 32 random bytes, written as hex.</summary>
        [Test]
        public void Issue_HandsOutLongTokensThatDoNotRepeat()
        {
            var tokens = new BackupDownloadTokens(new Clock(Start));

            var issued = Enumerable.Range(0, 100).Select(_ => tokens.Issue()).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(issued, Is.Unique);
                Assert.That(issued, Has.All.Matches<string>(t => t is { Length: 64 } && t.All(Uri.IsHexDigit)));
            });
        }

        /// <summary>
        /// Issuing sweeps out tokens nobody spent, so the table does not grow for as long as the
        /// process runs; a swept token is refused like any stale one.
        /// </summary>
        [Test]
        public void Issue_SweepsOutTokensNobodySpent()
        {
            var clock = new Clock(Start);
            var tokens = new BackupDownloadTokens(clock);
            var forgotten = tokens.Issue();

            clock.Now = Start.AddMinutes(3);
            var later = tokens.Issue();
            clock.Now = Start;

            Assert.Multiple(() =>
            {
                Assert.That(tokens.TryConsume(forgotten), Is.False, "swept when the next was issued");
                Assert.That(tokens.TryConsume(later), Is.True);
            });
        }

        private sealed class Clock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
