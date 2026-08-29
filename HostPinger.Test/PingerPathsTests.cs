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
        /// The default lives on <see cref="PingerPaths"/>, so an absent key resolves the same way
        /// wherever it is read from — and to somewhere the service can write, rather than to the
        /// content root, which is the read-only install folder on an installed machine.
        /// </summary>
        [Test]
        public void Resolve_FallsBackToTheDefaultDataDirectory_WhenNothingIsConfigured()
        {
            var paths = PingerPaths.Resolve(null, Path.GetTempPath());

            Assert.That(paths.DatabasePath,
                Is.EqualTo(Path.GetFullPath(
                    Path.Combine(PingerPaths.DefaultDataDirectory, PingerPaths.DatabaseFileName))));
        }

        /// <summary>
        /// The Windows default has to keep naming the directory the installed service already uses,
        /// or an upgrade would silently start over with an empty database.
        /// </summary>
        [Test]
        [Platform("Win")]
        public void DefaultDataDirectory_IsUnderProgramData_OnWindows()
        {
            Assert.That(PingerPaths.DefaultDataDirectory,
                Is.EqualTo(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "HostPinger")));
        }

        /// <summary>
        /// The container image mounts its volume here, so the path is part of the published
        /// contract rather than an implementation detail.
        /// </summary>
        [Test]
        [Platform(Exclude = "Win")]
        public void DefaultDataDirectory_IsUnderVarLib_OffWindows()
        {
            Assert.That(PingerPaths.DefaultDataDirectory, Is.EqualTo("/var/lib/hostpinger"));
        }

        [Test]
        public void Resolve_AnchorsARelativePathToTheContentRoot()
        {
            var contentRoot = Path.GetTempPath();

            var paths = PingerPaths.Resolve(Path.Combine("Data", "hostpinger.db"), contentRoot);

            Assert.That(paths.DatabasePath,
                Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Data", "hostpinger.db"))));
        }

        /// <summary>
        /// Expansion is what lets a deployment point the database at a machine-specific location —
        /// %ProgramData% on Windows, or a mount path passed in the environment on Linux.
        /// </summary>
        [Test]
        public void Resolve_ExpandsEnvironmentVariablesInTheDatabasePath()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"hostpinger-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(TestDirectoryVariable, directory);

            var paths = PingerPaths.Resolve($"%{TestDirectoryVariable}%/hostpinger.db", Path.GetTempPath());

            Assert.That(paths.DatabasePath, Is.EqualTo(Path.GetFullPath(Path.Combine(directory, "hostpinger.db"))));
        }
    }
}
