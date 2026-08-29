using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class UserSettingsStoreTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;
        private TestOptionsMonitor<PingerOptions> _pingerDefaults = null!;
        private TestOptionsMonitor<SecurityOptions> _securityDefaults = null!;
        private UserSettingsStore _store = null!;

        [SetUp]
        public void SetUp()
        {
            (_connection, _options) = TestDb.CreateInMemory();
            _pingerDefaults = new TestOptionsMonitor<PingerOptions>(new PingerOptions());
            _securityDefaults = new TestOptionsMonitor<SecurityOptions>(new SecurityOptions());
            _store = CreateStore();
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        /// <summary>
        /// What lets the pages save and redirect without waiting for anything: the moment a save
        /// returns, the store is already answering with it.
        /// </summary>
        [Test]
        public async Task Save_IsInForceTheMomentItReturns()
        {
            await _store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 45 });

            Assert.That(_store.CurrentPinger.IntervalSeconds, Is.EqualTo(45));
        }

        [Test]
        public async Task Save_WritesOnlyTheSuppliedSettings()
        {
            _pingerDefaults.CurrentValue = new PingerOptions { IntervalSeconds = 77 };

            await _store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 250 });

            var options = _store.CurrentPinger;
            Assert.Multiple(() =>
            {
                Assert.That(options.MaxDatabaseSizeMb, Is.EqualTo(250));
                Assert.That(options.IntervalSeconds, Is.EqualTo(77),
                    "settings without a stored row must fall through to the configured defaults");
            });
        }

        /// <summary>
        /// The Configuration page saves the whole pinger group at once, which must not disturb the
        /// database group stored alongside it.
        /// </summary>
        [Test]
        public async Task Save_WritesTheWholePingerGroupWithoutDisturbingTheDatabaseGroup()
        {
            await _store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 250 });

            await _store.SaveAsync(new PingerSettingsUpdate
            {
                IntervalSeconds = 90,
                TimeoutSeconds = 3,
                ResolveTimeoutSeconds = 8,
                RetryAttempts = 4,
            });

            var options = _store.CurrentPinger;
            Assert.Multiple(() =>
            {
                Assert.That(options.IntervalSeconds, Is.EqualTo(90));
                Assert.That(options.TimeoutSeconds, Is.EqualTo(3));
                Assert.That(options.ResolveTimeoutSeconds, Is.EqualTo(8));
                Assert.That(options.RetryAttempts, Is.EqualTo(4));
                Assert.That(options.MaxDatabaseSizeMb, Is.EqualTo(250));
            });
        }

        /// <summary>The restart of the service, played out: a fresh store reading the same table.</summary>
        [Test]
        public async Task LoadAsync_ReadsWhatAnEarlierStoreSaved()
        {
            await _store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 250 });
            await _store.SavePasswordHashAsync("stored-hash");

            var restarted = CreateStore();
            await restarted.LoadAsync();

            Assert.Multiple(() =>
            {
                Assert.That(restarted.CurrentPinger.MaxDatabaseSizeMb, Is.EqualTo(250));
                Assert.That(restarted.PasswordHash, Is.EqualTo("stored-hash"));
            });
        }

        /// <summary>
        /// One row gone strange is one setting falling back to its configured default, not a
        /// service that cannot know its interval.
        /// </summary>
        [Test]
        public async Task CurrentPinger_FallsBackToTheDefaultForARowThatDoesNotParse()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                db.UserSettings.Add(new UserSetting { Key = "Pinger:IntervalSeconds", Value = "soon" });
                await db.SaveChangesAsync();
            }

            await _store.LoadAsync();

            Assert.That(_store.CurrentPinger.IntervalSeconds,
                Is.EqualTo(new PingerOptions().IntervalSeconds));
        }

        /// <summary>
        /// The password lives under a key of its own in the same table, so neither group of
        /// settings can be saved over the other.
        /// </summary>
        [Test]
        public async Task SavePasswordHash_AndSave_LeaveEachOtherAlone()
        {
            var hash = PasswordHash.Hash("correct horse battery staple");

            await _store.SavePasswordHashAsync(hash);
            await _store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 90 });

            Assert.Multiple(() =>
            {
                Assert.That(_store.PasswordHash, Is.EqualTo(hash));
                Assert.That(_store.CurrentPinger.IntervalSeconds, Is.EqualTo(90));
            });
        }

        [Test]
        public async Task SavePasswordHash_LeavesNoPasswordInForceWhenGivenNull()
        {
            await _store.SavePasswordHashAsync(PasswordHash.Hash("correct horse battery staple"));
            await _store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 90 });

            await _store.SavePasswordHashAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(_store.PasswordHash, Is.Empty);
                Assert.That(_store.CurrentPinger.IntervalSeconds, Is.EqualTo(90),
                    "the pinger settings must survive it");
            });
        }

        [Test]
        public void PasswordHash_FallsThroughToConfigurationWhileNothingIsStored()
        {
            _securityDefaults.CurrentValue = new SecurityOptions { PasswordHash = "configured-elsewhere" };

            Assert.That(_store.PasswordHash, Is.EqualTo("configured-elsewhere"));
        }

        /// <summary>
        /// A stored row is what overrides configuration, so removing the password has to write one
        /// rather than delete one: deleting would uncover a hash configured in appsettings.json or
        /// the environment and leave the application locked by a password the page has just said
        /// was removed.
        /// </summary>
        [Test]
        public async Task SavePasswordHash_OverridesAPasswordConfiguredUnderneath()
        {
            _securityDefaults.CurrentValue = new SecurityOptions { PasswordHash = "configured-elsewhere" };

            await _store.SavePasswordHashAsync(null);

            Assert.That(_store.PasswordHash, Is.Empty);
        }

        private UserSettingsStore CreateStore() =>
            new(new TestDb.Factory(_options), _pingerDefaults, _securityDefaults);
    }
}
