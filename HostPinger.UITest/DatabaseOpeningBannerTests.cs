using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The banner the layout draws in place of the page until the start has opened the database:
    /// refused, waiting for the room an upgrade needs, and upgrading. Each test starts an instance of
    /// its own, since the run's is open by the time any test runs.
    /// </summary>
    public class DatabaseOpeningBannerTests : BrowserTest
    {
        /// <summary>The last migration with the attempts' times stored as text, 1.13.0's and 1.14.0's.</summary>
        private const string LastWithTextTimes = "20261016020813_AddRoundtripToPartialPingIndexes";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private ILocator Notice => Page.Locator(".database-notice");

        /// <summary>The Hosts page's heading, which is only drawn once the database is open.</summary>
        private ILocator HostsHeading => Page.Locator("h1", new PageLocatorOptions { HasText = "Hosts" });

        /// <summary>The lock button, which reads the settings and so is only drawn once they are loaded.</summary>
        private ILocator LockButton => Page.Locator("button[title='Unlock to make changes']");

        /// <summary>
        /// A database a newer build upgraded is refused, and the page says so and nothing else: no
        /// page, no lock, and no circuit to be had, for as long as the instance runs.
        /// </summary>
        [Test]
        public async Task ANewerDatabaseIsRefusedWithTheReasonInPlaceOfThePage()
        {
            await using var app = await WebApp.StartAsync(new WebAppStart
            {
                PrepareDatabase = path =>
                {
                    WebApp.SeedSettings(path);
                    Execute(path, "PRAGMA user_version = 99;");
                },
                ReadyAt = "Refused",
            });

            await Page.GotoAsync(app.BaseUrl);

            await Assertions.Expect(Notice).ToContainTextAsync("upgraded by a newer version of HostPinger");
            await AssertNoPageAsync();
            using var client = new HttpClient();
            using var hub = await client.GetAsync($"{app.BaseUrl}/_blazor");
            Assert.That((int)hub.StatusCode, Is.EqualTo(503), "a circuit could start");
        }

        /// <summary>
        /// An upgrade on a disk without the room it needs waits, and the page says how much it needs
        /// and how much the disk has, in place of the page.
        /// </summary>
        [Test]
        public async Task AnUpgradeWithoutTheRoomItNeedsSaysWhatItIsWaitingFor()
        {
            await using var app = await WebApp.StartAsync(new WebAppStart
            {
                PrepareDatabase = SeedBeforeTheConversion,
                Environment = new Dictionary<string, string>
                {
                    [DatabaseSizeLimit.DevelopmentFreeBytesVariable] = (1024 * 1024).ToString(),
                },
                ReadyAt = "WaitingForRoom",
            });

            await Page.GotoAsync(app.BaseUrl);

            await Assertions.Expect(Notice).ToContainTextAsync("waiting to upgrade its database");
            await Assertions.Expect(Notice).ToContainTextAsync("The disk has 1 MB");
            await AssertNoPageAsync();
        }

        /// <summary>
        /// An upgrade under way shows the time so far and what it has written, with a bar that moves
        /// without a measure; once it is done, the page reloads itself into the page, with the
        /// rollback banner over it. Held under way by holding the database's write lock, which the
        /// migration waits behind.
        /// </summary>
        [Test]
        public async Task AnUpgradeUnderWayShowsItsProgressAndThenThePage()
        {
            var databasePath = "";
            SqliteConnection? holder = null;
            try
            {
                await using var app = await WebApp.StartAsync(new WebAppStart
                {
                    PrepareDatabase = path =>
                    {
                        SeedBeforeTheConversion(path);
                        databasePath = path;
                        holder = new SqliteConnection($"Data Source={path};Pooling=False");
                        holder.Open();
                        using var begin = holder.CreateCommand();
                        begin.CommandText = "BEGIN IMMEDIATE;";
                        begin.ExecuteNonQuery();
                    },
                    ReadyAt = "Opening",
                });

                // /health says Opening a moment before the start knows it is upgrading, and a page
                // drawn in that moment only reloads ten seconds later, so loaded until it says so.
                var deadline = DateTime.UtcNow + Patience;
                do
                {
                    await Page.GotoAsync(app.BaseUrl);
                }
                while (!(await Notice.TextContentAsync() ?? "").Contains("upgrading its database") && DateTime.UtcNow < deadline);

                await Assertions.Expect(Notice).ToContainTextAsync("upgrading its database");
                await Assertions.Expect(Notice).ToContainTextAsync("so far");
                await Assertions.Expect(Notice.Locator(".progress-bar-animated")).ToBeVisibleAsync();
                await AssertNoPageAsync();

                // Let the migration through: the page reloads itself every few seconds meanwhile.
                holder!.Dispose();
                holder = null;

                await Assertions.Expect(HostsHeading).ToBeVisibleAsync(new() { Timeout = (float)Patience.TotalMilliseconds });
                await Assertions.Expect(Notice).ToContainTextAsync("1.14.0 and earlier cannot read");
                await Assertions.Expect(LockButton).ToBeVisibleAsync();
            }
            finally
            {
                holder?.Dispose();
            }
        }

        private async Task AssertNoPageAsync()
        {
            Assert.Multiple(async () =>
            {
                Assert.That(await HostsHeading.CountAsync(), Is.Zero, "the page was drawn");
                Assert.That(await LockButton.CountAsync(), Is.Zero, "the lock was drawn");
                Assert.That(await Page.Locator("meta[http-equiv='refresh']").CountAsync(), Is.EqualTo(1), "nothing reloads it");
            });
        }

        /// <summary>
        /// A database as 1.14.0 left it, with the times still text and the run's password and interval,
        /// so that opening it upgrades it.
        /// </summary>
        private static void SeedBeforeTheConversion(string path)
        {
            using (var db = new HostPingerDbContext(new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options))
            {
                db.GetService<IMigrator>().Migrate(LastWithTextTimes);
            }

            Execute(
                path,
                "INSERT INTO UserSettings (Key, Value) VALUES "
                + $"('{UserSettingsStore.PasswordHashKey}', '{PasswordHash.Hash(WebApp.Password)}'), "
                + $"('{PingerOptions.SectionName}:{nameof(PingerOptions.IntervalSeconds)}', '86400');");
        }

        private static void Execute(string path, string sql)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
