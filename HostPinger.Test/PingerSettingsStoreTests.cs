using HostPinger.Core.Options;
using Microsoft.Extensions.Configuration;

namespace HostPinger.Test
{
    public class PingerSettingsStoreTests
    {
        private string _directory = string.Empty;
        private PingerPaths _paths = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _paths = new PingerPaths(
                Path.Combine(_directory, "hostpinger.db"),
                Path.Combine(_directory, PingerPaths.SettingsFileName));
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task Save_WritesOnlyTheSuppliedSettings()
        {
            await new PingerSettingsStore(_paths).SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 250 });

            var options = Bind();
            Assert.Multiple(() =>
            {
                Assert.That(options.MaxDatabaseSizeMb, Is.EqualTo(250));
                Assert.That(options.IntervalSeconds, Is.EqualTo(new PingerOptions().IntervalSeconds),
                    "settings the overlay omits must fall through to appsettings.json");
            });
        }

        [Test]
        public async Task Save_PreservesSettingsWrittenEarlier()
        {
            var store = new PingerSettingsStore(_paths);
            await store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 250 });

            await store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 90 });

            var options = Bind();
            Assert.Multiple(() =>
            {
                Assert.That(options.MaxDatabaseSizeMb, Is.EqualTo(250));
                Assert.That(options.IntervalSeconds, Is.EqualTo(90));
            });
        }

        /// <summary>
        /// The Configuration page saves the whole pinger group at once, which must not disturb the
        /// database group stored alongside it.
        /// </summary>
        [Test]
        public async Task Save_WritesTheWholePingerGroupWithoutDisturbingTheDatabaseGroup()
        {
            var store = new PingerSettingsStore(_paths);
            await store.SaveAsync(new PingerSettingsUpdate { MaxDatabaseSizeMb = 250 });

            await store.SaveAsync(new PingerSettingsUpdate
            {
                IntervalSeconds = 90,
                TimeoutSeconds = 3,
                ResolveTimeoutSeconds = 8,
                RetryAttempts = 4,
            });

            var options = Bind();
            Assert.Multiple(() =>
            {
                Assert.That(options.IntervalSeconds, Is.EqualTo(90));
                Assert.That(options.TimeoutSeconds, Is.EqualTo(3));
                Assert.That(options.ResolveTimeoutSeconds, Is.EqualTo(8));
                Assert.That(options.RetryAttempts, Is.EqualTo(4));
                Assert.That(options.MaxDatabaseSizeMb, Is.EqualTo(250));
            });
        }

        [Test]
        public async Task Save_StartsOverWhenTheFileIsUnreadable()
        {
            await File.WriteAllTextAsync(_paths.SettingsPath, "{ not json");

            await new PingerSettingsStore(_paths).SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 45 });

            Assert.That(Bind().IntervalSeconds, Is.EqualTo(45));
        }

        /// <summary>
        /// The password lives in a section of its own in the same file, so neither group of
        /// settings can be saved over the other.
        /// </summary>
        [Test]
        public async Task SavePasswordHash_AndSave_LeaveEachOtherAlone()
        {
            var store = new PingerSettingsStore(_paths);
            var hash = PasswordHash.Hash("correct horse battery staple");

            await store.SavePasswordHashAsync(hash);
            await store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 90 });

            Assert.Multiple(() =>
            {
                Assert.That(BindSecurity().PasswordHash, Is.EqualTo(hash));
                Assert.That(Bind().IntervalSeconds, Is.EqualTo(90));
            });
        }

        [Test]
        public async Task SavePasswordHash_LeavesNoPasswordInForceWhenGivenNull()
        {
            var store = new PingerSettingsStore(_paths);
            await store.SavePasswordHashAsync(PasswordHash.Hash("correct horse battery staple"));
            await store.SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 90 });

            await store.SavePasswordHashAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(BindSecurity().PasswordHash, Is.Empty);
                Assert.That(Bind().IntervalSeconds, Is.EqualTo(90), "the pinger settings must survive it");
            });
        }

        /// <summary>
        /// The overlay is the last configuration source, so removing the password has to write the
        /// key rather than delete it: deleting would uncover a hash configured in appsettings.json
        /// or the environment and leave the application locked by a password the page has just
        /// said was removed.
        /// </summary>
        [Test]
        public async Task SavePasswordHash_OverridesAPasswordConfiguredUnderneathTheOverlay()
        {
            var appSettingsPath = Path.Combine(_directory, "appsettings.json");
            await File.WriteAllTextAsync(
                appSettingsPath,
                $$$"""{"{{{SecurityOptions.SectionName}}}": {"PasswordHash": "configured-elsewhere"}}""");

            await new PingerSettingsStore(_paths).SavePasswordHashAsync(null);

            // Layered the way Program.cs layers them: the overlay is added last and so wins.
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(appSettingsPath)
                .AddJsonFile(_paths.SettingsPath)
                .Build();
            var options = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>();

            Assert.That(options?.PasswordHash, Is.Empty);
        }

        /// <summary>Reads the overlay the way the application does, proving the JSON shape matches.</summary>
        private PingerOptions Bind()
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(_paths.SettingsPath).Build();
            return configuration.GetSection(PingerOptions.SectionName).Get<PingerOptions>() ?? new PingerOptions();
        }

        private SecurityOptions BindSecurity()
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(_paths.SettingsPath).Build();
            return configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        }
    }
}
