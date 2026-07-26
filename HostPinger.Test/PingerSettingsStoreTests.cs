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

        [Test]
        public async Task Save_StartsOverWhenTheFileIsUnreadable()
        {
            await File.WriteAllTextAsync(_paths.SettingsPath, "{ not json");

            await new PingerSettingsStore(_paths).SaveAsync(new PingerSettingsUpdate { IntervalSeconds = 45 });

            Assert.That(Bind().IntervalSeconds, Is.EqualTo(45));
        }

        /// <summary>Reads the overlay the way the application does, proving the JSON shape matches.</summary>
        private PingerOptions Bind()
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(_paths.SettingsPath).Build();
            return configuration.GetSection(PingerOptions.SectionName).Get<PingerOptions>() ?? new PingerOptions();
        }
    }
}
