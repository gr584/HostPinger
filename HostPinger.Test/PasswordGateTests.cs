using System.Security.Claims;
using HostPinger.Core.Options;
using HostPinger.Security;
using Microsoft.Data.Sqlite;

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

        private SqliteConnection _connection = null!;
        private TestOptionsMonitor<SecurityOptions> _configured = null!;
        private UserSettingsStore _store = null!;
        private PasswordGate _gate = null!;

        [SetUp]
        public void SetUp()
        {
            (_connection, var options) = TestDb.CreateInMemory();
            _configured = new TestOptionsMonitor<SecurityOptions>(new SecurityOptions());
            _store = new UserSettingsStore(
                new TestDb.Factory(options),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions()),
                _configured);
            _gate = new PasswordGate(_store);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

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
        public async Task IsUnlocked_IsFalseForAnAnonymousBrowserOnceAPasswordIsSet()
        {
            await SetPasswordAsync(Password);

            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsPasswordSet, Is.True);
                Assert.That(_gate.IsUnlocked(null), Is.False);
                Assert.That(_gate.IsUnlocked(Anonymous), Is.False);
            });
        }

        [Test]
        public async Task IsUnlocked_IsTrueForTheBrowserItSignedIn()
        {
            await SetPasswordAsync(Password);

            Assert.That(_gate.IsUnlocked(_gate.CreatePrincipal()), Is.True);
        }

        /// <summary>
        /// Changing the password turns out every browser that unlocked under the old one, which is
        /// the whole reason the cookie carries a stamp.
        /// </summary>
        [Test]
        public async Task IsUnlocked_IsFalseForAnUnlockIssuedUnderThePreviousPassword()
        {
            await SetPasswordAsync(Password);
            var issuedBefore = _gate.CreatePrincipal();

            await SetPasswordAsync("something else entirely");

            Assert.That(_gate.IsUnlocked(issuedBefore), Is.False);
        }

        [Test]
        public async Task IsUnlocked_IsTrueForEverybodyAgainOnceThePasswordIsRemoved()
        {
            await SetPasswordAsync(Password);
            var issuedBefore = _gate.CreatePrincipal();

            await _store.SavePasswordHashAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsUnlocked(Anonymous), Is.True);
                Assert.That(_gate.IsUnlocked(issuedBefore), Is.True);
            });
        }

        /// <summary>
        /// The page that changes the password signs its browser back in straight after the save.
        /// That only works because a save is in force the moment it returns: a principal stamped
        /// from a stale answer would lock the person changing the password out of their own
        /// browser.
        /// </summary>
        [Test]
        public async Task CreatePrincipal_StampsThePasswordJustSaved()
        {
            await SetPasswordAsync(Password);
            var issuedBefore = _gate.CreatePrincipal();

            await SetPasswordAsync("something else entirely");
            var issuedOnSave = _gate.CreatePrincipal();

            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsUnlocked(issuedOnSave), Is.True);
                Assert.That(_gate.IsUnlocked(issuedBefore), Is.False);
            });
        }

        /// <summary>
        /// A password can also arrive from appsettings.json or the environment rather than the
        /// store, and the gate must enforce it the same way.
        /// </summary>
        [Test]
        public void IsUnlocked_HonoursAPasswordConfiguredRatherThanStored()
        {
            _configured.CurrentValue = new SecurityOptions { PasswordHash = PasswordHash.Hash(Password) };

            Assert.Multiple(() =>
            {
                Assert.That(_gate.IsUnlocked(Anonymous), Is.False);
                Assert.That(_gate.Verify(Password), Is.True);
            });
        }

        [Test]
        public async Task Verify_AnswersForThePasswordInForce()
        {
            await SetPasswordAsync(Password);

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

        private Task SetPasswordAsync(string password) =>
            _store.SavePasswordHashAsync(PasswordHash.Hash(password));
    }
}
