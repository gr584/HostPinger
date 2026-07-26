using HostPinger.Core.Options;

namespace HostPinger.Test
{
    public class PingerPathsTests
    {
        [Test]
        public void Resolve_PutsTheSettingsFileNextToTheDatabase()
        {
            var contentRoot = Path.GetTempPath();

            var paths = PingerPaths.Resolve(Path.Combine("Data", "hostpinger.db"), contentRoot);

            Assert.Multiple(() =>
            {
                Assert.That(paths.DatabasePath,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Data", "hostpinger.db"))));
                Assert.That(paths.SettingsPath,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(contentRoot, "Data", PingerPaths.SettingsFileName))));
            });
        }
    }
}
