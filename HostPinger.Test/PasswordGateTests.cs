using System.Security.Claims;
using HostPinger.Core.Options;
using HostPinger.Security;

namespace HostPinger.Test
{
    /// <summary>
    /// The one predicate behind every action that changes a host or a setting, so each case here is
    /// a way in or a way of being kept out.
    /// </summary>
    public class PasswordGateTests
    {
        private const string Password = "correct horse battery staple";

        private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

        private TestOptionsMonitor<SecurityOptions> _options = null!;
        private PasswordGate _gate = null!;

        [SetUp]
        public void SetUp()
        {
            _options = new TestOptionsMonitor<SecurityOptions>(new SecurityOptions());
            _gate = new PasswordGate(_options);
        }

        /// <summary>How every install starts, and how it stays until someone sets a password.</summary>
        [Test]
        public void IsUnlocked_IsTrueForAnybodyWhileNoPasswordIsSet()
        {
            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsPasswordSet, Is.False);
                Assert.That(_gate.IsUnlocked(null), Is.True);
                Assert.That(_gate.IsUnlocked(Anonymous), Is.True);
            });
        }

        [Test]
        public void IsUnlocked_IsFalseForAnAnonymousBrowserOnceAPasswordIsSet()
        {
            SetPassword(Password);

            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsPasswordSet, Is.True);
                Assert.That(_gate.IsUnlocked(null), Is.False);
                Assert.That(_gate.IsUnlocked(Anonymous), Is.False);
            });
        }

        [Test]
        public void IsUnlocked_IsTrueForTheBrowserItSignedIn()
        {
            SetPassword(Password);

            Assert.That(_gate.IsUnlocked(_gate.CreatePrincipal()), Is.True);
        }

        /// <summary>
        /// Changing the password turns out every browser that unlocked under the old one, which is
        /// the whole reason the cookie carries a stamp.
        /// </summary>
        [Test]
        public void IsUnlocked_IsFalseForAnUnlockIssuedUnderThePreviousPassword()
        {
            SetPassword(Password);
            var issuedBefore = _gate.CreatePrincipal();

            SetPassword("something else entirely");

            Assert.That(_gate.IsUnlocked(issuedBefore), Is.False);
        }

        [Test]
        public void IsUnlocked_IsTrueForEverybodyAgainOnceThePasswordIsRemoved()
        {
            SetPassword(Password);
            var issuedBefore = _gate.CreatePrincipal();
            _options.CurrentValue = new SecurityOptions();

            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsUnlocked(Anonymous), Is.True);
                Assert.That(_gate.IsUnlocked(issuedBefore), Is.True);
            });
        }

        /// <summary>
        /// The page that changes the password signs the browser back in from the hash it has just
        /// written, because the configuration reload has not caught up yet. Stamping that unlock
        /// from the stored value instead would lock the person changing the password out of their
        /// own browser the moment it did.
        /// </summary>
        [Test]
        public void CreatePrincipal_TakesTheHashItIsGivenOverTheOneStillConfigured()
        {
            SetPassword(Password);
            var replacement = PasswordHash.Hash("something else entirely");

            var issuedOnSave = _gate.CreatePrincipal(replacement);
            Assert.That(_gate.IsUnlocked(issuedOnSave), Is.False, "it must not unlock under the old password");

            _options.CurrentValue = new SecurityOptions { PasswordHash = replacement };
            Assert.That(_gate.IsUnlocked(issuedOnSave), Is.True, "and must unlock once the reload arrives");
        }

        /// <summary>
        /// The page that saves a password redirects straight afterwards, and the page it lands on
        /// reads this gate. Without the wait it reads the state that was just replaced: removing a
        /// password lands on a Configuration page still reporting one.
        /// </summary>
        [Test]
        public async Task WaitForSave_ReturnsOnceTheReloadReportsWhatWasSaved()
        {
            SetPassword(Password);
            var replacement = PasswordHash.Hash("something else entirely");

            var waiting = _gate.WaitForSaveAsync(replacement);
            Assert.That(waiting.IsCompleted, Is.False, "the configuration has not reloaded yet");

            _options.CurrentValue = new SecurityOptions { PasswordHash = replacement };

            await waiting;
            Assert.That(_gate.IsUnlocked(_gate.CreatePrincipal()), Is.True);
        }

        [Test]
        public async Task WaitForSave_ReturnsAtOnceWhenTheReloadHasAlreadyLanded()
        {
            SetPassword(Password);

            var waiting = _gate.WaitForSaveAsync(_options.CurrentValue.PasswordHash);

            Assert.That(waiting.IsCompleted, Is.True);
            await waiting;
        }

        /// <summary>Removal writes the key as empty, so that is what it waits to see.</summary>
        [Test]
        public async Task WaitForSave_TreatsAnEmptyHashAsTheRemovalLanding()
        {
            SetPassword(Password);

            var waiting = _gate.WaitForSaveAsync(null);
            _options.CurrentValue = new SecurityOptions { PasswordHash = string.Empty };

            await waiting;
            Assert.That(_gate.IsPasswordSet, Is.False);
        }

        /// <summary>
        /// A reload that never arrives costs the page a stale render, which it recovers from on its
        /// own refresh; holding the request open until it gives up would be worse.
        /// </summary>
        [Test]
        public void WaitForSave_GivesUpQuietlyWhenTheReloadNeverArrives()
        {
            SetPassword(Password);

            Assert.That(
                async () => await _gate.WaitForSaveAsync(
                    PasswordHash.Hash("never written"),
                    TimeSpan.FromMilliseconds(50)),
                Throws.Nothing);
        }

        [Test]
        public void Verify_AnswersForThePasswordInForce()
        {
            SetPassword(Password);

            Assert.Multiple(() =>
            {
                Assert.That(_gate.Verify(Password), Is.True);
                Assert.That(_gate.Verify("hunter2"), Is.False);
            });
        }

        [Test]
        public void Verify_IsFalseWhileNoPasswordIsSet()
        {
            Assert.That(_gate.Verify(string.Empty), Is.False);
        }

        private void SetPassword(string password) =>
            _options.CurrentValue = new SecurityOptions { PasswordHash = PasswordHash.Hash(password) };
    }
}
