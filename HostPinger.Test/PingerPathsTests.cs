using HostPinger.Core.Options;

namespace HostPinger.Test
{
    public class PingerPathsTests
    {
        private const string TestDirectoryVariable = "HOSTPINGER_TEST_DIR";

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(TestDirectoryVariable, null);
        }

        /// <summary>
        /// Both defaults live on <see cref="PingerPaths"/>, so an absent key resolves the same way
        /// wherever it is read from.
        /// </summary>
        [Test]
        public void Resolve_FallsBackToBothDefaultFileNames_WhenNothingIsConfigured()
        {
            var contentRoot = Path.GetTempPath();

            var paths = PingerPaths.Resolve(null, null, contentRoot);

            Assert.Multiple(() =>
            {
                Assert.That(paths.DatabasePath,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, PingerPaths.DatabaseFileName))));
                Assert.That(paths.SettingsPath,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, PingerPaths.SettingsFileName))));
            });
        }

        [Test]
        public void Resolve_ExpandsEnvironmentVariablesInTheDatabasePath()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hostpinger-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(TestDirectoryVariable, directory);

            var paths = PingerPaths.Resolve(
                $"%{TestDirectoryVariable}%/hostpinger.db",
                null,
                Path.GetTempPath());

            Assert.That(paths.DatabasePath, Is.EqualTo(Path.GetFullPath(Path.Combine(directory, "hostpinger.db"))));
        }

        [Test]
        public void Resolve_PutsTheSettingsFileNextToTheDatabase_WhenNoPathIsConfigured()
        {
            var contentRoot = Path.GetTempPath();

            var paths = PingerPaths.Resolve(Path.Combine("Data", "hostpinger.db"), null, contentRoot);

            Assert.Multiple(() =>
            {
                Assert.That(paths.DatabasePath,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Data", "hostpinger.db"))));
                Assert.That(paths.SettingsPath,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Data", PingerPaths.SettingsFileName))));
            });
        }

        /// <summary>An appsettings.json predating the key must keep working unchanged.</summary>
        [Test]
        public void Resolve_FallsBackToTheDatabaseDirectory_WhenTheConfiguredPathIsBlank()
        {
            var contentRoot = Path.GetTempPath();

            var paths = PingerPaths.Resolve(Path.Combine("Data", "hostpinger.db"), "   ", contentRoot);

            Assert.That(paths.SettingsPath,
                Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Data", PingerPaths.SettingsFileName))));
        }

        [Test]
        public void Resolve_UsesTheConfiguredSettingsPath()
        {
            var contentRoot = Path.GetTempPath();
            var configured = Path.Combine(Path.GetTempPath(), "elsewhere", "custom.json");

            var paths = PingerPaths.Resolve(Path.Combine("Data", "hostpinger.db"), configured, contentRoot);

            Assert.That(paths.SettingsPath, Is.EqualTo(Path.GetFullPath(configured)));
        }

        [Test]
        public void Resolve_ResolvesARelativeSettingsPathAgainstTheContentRoot()
        {
            var contentRoot = Path.GetTempPath();

            var paths = PingerPaths.Resolve("Data/hostpinger.db", "Config/custom.json", contentRoot);

            Assert.That(paths.SettingsPath,
                Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Config", "custom.json"))));
        }

        /// <summary>The shipped appsettings.json points the overlay at %ProgramData%.</summary>
        [Test]
        public void Resolve_ExpandsEnvironmentVariablesInTheSettingsPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hostpinger-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(TestDirectoryVariable, directory);

            var paths = PingerPaths.Resolve(
                "Data/hostpinger.db",
                $"%{TestDirectoryVariable}%/custom.json",
                Path.GetTempPath());

            Assert.That(paths.SettingsPath, Is.EqualTo(Path.GetFullPath(Path.Combine(directory, "custom.json"))));
        }

        /// <summary>
        /// The overlay location is deliberately independent of the database location: moving the
        /// database must not drag the settings file somewhere the service cannot write, and the
        /// overlay cannot be the thing that says where the database is.
        /// </summary>
        [Test]
        public void Resolve_KeepsTheSettingsPath_WhenTheDatabaseMoves()
        {
            var contentRoot = Path.GetTempPath();
            var configured = Path.Combine(Path.GetTempPath(), "config", PingerPaths.SettingsFileName);

            var near = PingerPaths.Resolve("Data/hostpinger.db", configured, contentRoot);
            var far = PingerPaths.Resolve(
                Path.Combine(Path.GetTempPath(), "elsewhere", "hostpinger.db"),
                configured,
                contentRoot);

            Assert.Multiple(() =>
            {
                Assert.That(near.SettingsPath, Is.EqualTo(far.SettingsPath));
                Assert.That(near.DatabasePath, Is.Not.EqualTo(far.DatabasePath));
            });
        }
    }
}
