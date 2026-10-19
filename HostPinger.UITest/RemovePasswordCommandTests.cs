using System.Diagnostics;
using System.Reflection;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.UITest
{
    /// <summary>
    /// <c>HostPinger --remove-password</c>, run as somebody locked out runs it: the built application,
    /// pointed at its database the way a service is. What the command writes is settled in
    /// RemovePasswordTests; what only the real executable can settle is the way there — that the
    /// switch is recognised and taken out before the configuration reads the arguments, that the
    /// database is found where the configuration says, and that the command reports and exits
    /// instead of starting the service. No browser is needed, but this is the project that knows
    /// where the built application is.
    /// </summary>
    public class RemovePasswordCommandTests
    {
        private string _directory = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task RemovePassword_ClearsThePasswordOfTheConfiguredDatabaseAndExits()
        {
            var databasePath = Path.Combine(_directory, PingerPaths.DatabaseFileName);
            await using (var db = new HostPingerDbContext(Options(databasePath)))
            {
                await db.Database.MigrateAsync();
                db.UserSettings.Add(new UserSetting { Key = UserSettingsStore.PasswordHashKey, Value = "a-forgotten-hash" });
                await db.SaveChangesAsync();
            }

            var (exitCode, output) = await RunAsync(databasePath, "--remove-password");

            Assert.Multiple(async () =>
            {
                Assert.That(exitCode, Is.Zero, output);
                Assert.That(output, Does.Contain($"Password removed from {databasePath}."));
                await using var db = new HostPingerDbContext(Options(databasePath));
                Assert.That(
                    await db.UserSettings.Where(s => s.Key == UserSettingsStore.PasswordHashKey).Select(s => s.Value).SingleAsync(),
                    Is.Empty);
            });
        }

        /// <summary>
        /// The switch is taken out before the configuration reads the arguments, so what follows
        /// it is still read as configuration — here the database path, which wins over the one in
        /// the environment. Left in, the switch would swallow the argument after it as its value,
        /// and the command would clear a database other than the one named.
        /// </summary>
        [Test]
        public async Task RemovePassword_LeavesTheArgumentsAfterItToTheConfiguration()
        {
            var databasePath = Path.Combine(_directory, PingerPaths.DatabaseFileName);
            var decoyPath = Path.Combine(_directory, "decoy", PingerPaths.DatabaseFileName);

            var (exitCode, output) = await RunAsync(decoyPath, "--remove-password", $"--Pinger:DatabasePath={databasePath}");

            Assert.Multiple(async () =>
            {
                Assert.That(exitCode, Is.Zero, output);
                Assert.That(output, Does.Contain($"Password removed from {databasePath}."));
                Assert.That(File.Exists(decoyPath), Is.False, "the environment's database was left alone");
                await using var db = new HostPingerDbContext(Options(databasePath));
                Assert.That(
                    await db.UserSettings.Where(s => s.Key == UserSettingsStore.PasswordHashKey).Select(s => s.Value).SingleAsync(),
                    Is.Empty);
            });
        }

        private static DbContextOptions<HostPingerDbContext> Options(string databasePath) =>
            new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite($"Data Source={databasePath}").Options;

        /// <summary>
        /// Runs the built application with <paramref name="arguments"/>, its database at
        /// <paramref name="databasePath"/>, and waits for it to exit — which a command does by
        /// itself, and a service started by mistake never would, so the wait is bounded.
        /// </summary>
        private static async Task<(int ExitCode, string Output)> RunAsync(string databasePath, params string[] arguments)
        {
            var directory = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "WebAppDirectory").Value!;
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(Path.Combine(directory, "HostPinger.dll"));
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["Pinger__DatabasePath"] = databasePath;

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the application.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("The command did not exit; it started the service instead.");
            }

            return (process.ExitCode, await output + await errors);
        }
    }
}
