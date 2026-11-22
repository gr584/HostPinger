using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HostPinger.Test
{
    /// <summary>
    /// The recovery for a lost password, <see cref="UserSettingsStore.RemovePasswordAsync"/>, which
    /// <c>HostPinger --remove-password</c> runs instead of the service. Somebody running it is locked
    /// out, so it has to work on whatever database it finds — including none — and leave one that
    /// the service, once started, reads as having no password at all.
    /// </summary>
    public class RemovePasswordTests
    {
        /// <summary>The last migration with the attempts' times stored as text, 1.13.0's and 1.14.0's.</summary>
        private const string LastWithTextTimes = "20261016020813_AddRoundtripToPartialPingIndexes";

        private string _directory = null!;
        private string _databasePath = null!;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"hostpinger-remove-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "hostpinger.db");
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }

        /// <summary>
        /// A database that needs upgrading, on a disk without the room the upgrade needs, is left as it
        /// is, and the command says why rather than filling the disk partway through: the rule the
        /// service waits on.
        /// </summary>
        [Test]
        public async Task RemovePasswordAsync_RefusesAnUpgradeTheDiskHasNoRoomFor()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            }

            var refused = await Assert.ThrowsAsync<MigrationRoomException>(
                () => UserSettingsStore.RemovePasswordAsync(_databasePath, () => 1024 * 1024));

            await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            Assert.Multiple(async () =>
            {
                Assert.That(refused!.NeededBytes, Is.GreaterThan(refused.FreeBytes));
                Assert.That(await check.Database.GetPendingMigrationsAsync(), Is.Not.Empty, "upgraded without the room");
            });
        }

        /// <summary>
        /// With the room, the same database is upgraded and its password removed, as it always was;
        /// and a database with nothing to upgrade needs none.
        /// </summary>
        [Test]
        public async Task RemovePasswordAsync_UpgradesWhenTheDiskHasTheRoomAndNeedsNoneOtherwise()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync(LastWithTextTimes);
            }

            await UserSettingsStore.RemovePasswordAsync(_databasePath, () => long.MaxValue);
            await UserSettingsStore.RemovePasswordAsync(_databasePath, () => 0);

            await using var check = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            Assert.That(await check.Database.GetPendingMigrationsAsync(), Is.Empty);
        }

        /// <summary>A password set on the Password page is gone once the service reads its settings again.</summary>
        [Test]
        public async Task RemovePasswordAsync_RemovesAStoredPassword()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await HostPingerDatabase.InitializeAsync(db);
                await UserSettingsStore.StageAsync(db, UserSettingsStore.PasswordHashKey, "stored-hash");
                await db.SaveChangesAsync();
            }

            await UserSettingsStore.RemovePasswordAsync(_databasePath);

            Assert.That(await PasswordHashAsync(configured: null), Is.Empty);
        }

        /// <summary>
        /// A password configured in appsettings.json or the environment is cancelled too: the empty
        /// row is a stored setting, and a stored setting wins. Deleting the row instead would let
        /// the configured password straight back in.
        /// </summary>
        [Test]
        public async Task RemovePasswordAsync_OverridesAConfiguredPassword()
        {
            await UserSettingsStore.RemovePasswordAsync(_databasePath);

            Assert.That(await PasswordHashAsync(configured: "configured-hash"), Is.Empty);
        }

        /// <summary>
        /// With no database yet, one is created, fully migrated, that says there is no password,
        /// rather than the command failing somebody who is locked out.
        /// </summary>
        [Test]
        public async Task RemovePasswordAsync_CreatesAndMigratesADatabaseThatIsNotThereYet()
        {
            await UserSettingsStore.RemovePasswordAsync(_databasePath);

            await using var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath));
            Assert.Multiple(async () =>
            {
                Assert.That(await db.Database.GetPendingMigrationsAsync(), Is.Empty);
                Assert.That(await PasswordHashAsync(configured: "configured-hash"), Is.Empty);
            });
        }

        /// <summary>
        /// A database an older build left, from before settings lived in it, is migrated first,
        /// so that there is a table to write the row into.
        /// </summary>
        [Test]
        public async Task RemovePasswordAsync_MigratesADatabaseAnOlderBuildLeft()
        {
            await using (var db = new HostPingerDbContext(HostPingerDatabase.Options(_databasePath)))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260811051236_AddResolverErrors");
            }

            await UserSettingsStore.RemovePasswordAsync(_databasePath);

            Assert.That(await PasswordHashAsync(configured: "configured-hash"), Is.Empty);
        }

        /// <summary>The password in force for a service started on the database, with <paramref name="configured"/> configured.</summary>
        private async Task<string?> PasswordHashAsync(string? configured)
        {
            var store = new UserSettingsStore(
                new TestDb.Factory(HostPingerDatabase.Options(_databasePath)),
                new TestOptionsMonitor<PingerOptions>(new PingerOptions()),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions { PasswordHash = configured }));
            await store.LoadAsync();
            return store.PasswordHash;
        }
    }
}
