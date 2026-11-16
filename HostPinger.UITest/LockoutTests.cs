using System.Text.RegularExpressions;
using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// What a backup or a restore in one browser does to another: from its button to its end no
    /// browser can change anything, and the other is shown why. That nothing is written meanwhile
    /// is settled in ChangeLockoutTests and the stores' own tests; what only two browsers can
    /// settle is that the button is what takes the hold, that leaving the overlay gives it back,
    /// and that the other browser's pages hold their controls back, close their dialogs and say
    /// what is under way.
    /// </summary>
    /// <remarks>
    /// Not parallel with anything, for the reason the lockout exists: while one of these holds it,
    /// no other fixture's change would be made.
    /// </remarks>
    [NonParallelizable]
    public class LockoutTests : BrowserTest
    {
        /// <summary>A host of this fixture's own, for the other browser to have something to edit.</summary>
        private const string HeldName = "lockout-held";

        private const string Refused = "A restore is under way. Nothing can be changed until it has finished.";

        private IBrowserContext? _otherContext;

        /// <summary>The other browser: a context of its own, so an unlock of its own.</summary>
        private IPage Other { get; set; } = null!;

        [SetUp]
        public async Task OpenTheOtherBrowserAsync()
        {
            _otherContext = await UiTestRun.Browser.NewContextAsync();
            Other = await _otherContext.NewPageAsync();
        }

        [TearDown]
        public async Task CloseTheOtherBrowserAsync()
        {
            // The overlay's hold goes with its page, if a test stopped with it open.
            if (Page is not null)
            {
                await Page.CloseAsync();
            }
            if (_otherContext is not null)
            {
                await _otherContext.DisposeAsync();
            }
        }

        /// <summary>
        /// Pressing Restore backup… is what holds every other browser out, before anything is
        /// asked or chosen: the dialog another browser had open is closed, with what was typed
        /// into it, its controls are held back, and both its page and the top row say a restore is
        /// under way. The step the restore waits at says that it closes itself, and when. Leaving
        /// the overlay gives everything back, and the host is as it was.
        /// </summary>
        [Test]
        public async Task Restore_FromItsButtonHoldsAnotherBrowserOutUntilItIsLeft()
        {
            await SeedAsync(HeldName);
            await UnlockOtherAsync();
            await ClickUntilAsync(OtherRow(HeldName).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }), OtherDialog);
            await Other.Locator("#host-name").FillAsync("typed and thrown away");
            await UnlockedConfigurationPageAsync();

            await ClickUntilAsync(Button("Restore backup…"), Overlay);

            await Assertions.Expect(OtherDialog).ToBeHiddenAsync();
            await Assertions.Expect(OtherStatus).ToHaveTextAsync("Restore in progress");
            await Assertions.Expect(Other.Locator(".lockout-notice"))
                .ToHaveTextAsync("A restore is under way, so hosts cannot be changed until it has finished.");
            await Assertions.Expect(OtherButton("Add host")).ToHaveCountAsync(0);
            await Assertions.Expect(OtherRow(HeldName).GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
            await Assertions.Expect(OtherRow(HeldName).GetByRole(AriaRole.Switch)).ToBeDisabledAsync();
            await Assertions.Expect(OtherButton("Unlocked")).ToHaveCountAsync(0);

            // This browser's own top row says it too, and its step how long it will wait.
            await Assertions.Expect(Page.Locator(".lockout-status")).ToHaveTextAsync("Restore in progress");
            await Assertions.Expect(Dialog.Locator(".step-wait")).ToContainTextAsync("No browser can change anything while this is open.");
            await Assertions.Expect(Dialog.Locator(".step-left")).ToHaveTextAsync(new Regex(@"^4:\d\d$"));

            await Button(Dialog, "Cancel").ClickAsync();

            await Assertions.Expect(OtherStatus).ToHaveCountAsync(0);
            await Assertions.Expect(OtherButton("Unlocked")).ToBeVisibleAsync();
            await Assertions.Expect(OtherButton("Add host")).ToBeVisibleAsync();
            await Assertions.Expect(OtherRow(HeldName).GetByRole(AriaRole.Switch)).ToBeEnabledAsync();
            await Assertions.Expect(Other.Locator(".lockout-notice")).ToHaveCountAsync(0);
            await Assertions.Expect(OtherRow(HeldName)).ToContainTextAsync(HeldName);
            await Assertions.Expect(Other.GetByText("typed and thrown away")).ToHaveCountAsync(0);
        }

        /// <summary>
        /// The change the lockout is for: while a restore is under way from one browser, another
        /// cannot change the password, which the restore would otherwise go on to replace without
        /// asking again. The form was on screen before the restore's button was pressed, so it is
        /// the posting that is refused, and the page says so when it is opened meanwhile. The
        /// password is the run's own afterwards: the browser that was restoring is still unlocked.
        /// </summary>
        [Test]
        public async Task Restore_UnderWayKeepsAnotherBrowserFromChangingThePassword()
        {
            await UnlockOtherAsync();
            await Other.GotoAsync($"{BaseUrl}/password");
            await Assertions.Expect(Other.Locator(".lockout-notice")).ToHaveCountAsync(0);
            await Other.Locator("#current-password").FillAsync(WebApp.Password);
            await Other.Locator("#new-password").FillAsync("set while a restore was under way");
            await Other.Locator("#confirm-password").FillAsync("set while a restore was under way");
            await UnlockedConfigurationPageAsync();
            await ClickUntilAsync(Button("Restore backup…"), Overlay);

            await OtherButton("Change password").ClickAsync();

            await Assertions.Expect(Other.Locator(".alert-danger")).ToHaveTextAsync(Refused);
            await Assertions.Expect(Other.Locator(".lockout-notice")).ToHaveTextAsync(Refused);
            await Assertions.Expect(Other).ToHaveURLAsync($"{BaseUrl}/password");

            await Button(Dialog, "Cancel").ClickAsync();
            await Assertions.Expect(Page.Locator(".lockout-status")).ToHaveCountAsync(0);
            await Other.GotoAsync($"{BaseUrl}/password");
            await Assertions.Expect(Other.Locator(".lockout-notice")).ToHaveCountAsync(0);
            await GoAsync("/configuration");
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
        }

        /// <summary>
        /// A backup holds every other browser out as a restore does, from its button, here while
        /// it waits for the current password: the other browser's settings can be read and not
        /// saved, it cannot start a backup or a restore of its own, and it is told a backup is
        /// under way. Leaving the overlay gives everything back with nobody's unlock touched.
        /// </summary>
        [Test]
        public async Task Backup_FromItsButtonHoldsAnotherBrowserOutUntilItIsLeft()
        {
            await UnlockOtherAsync();
            await Other.GotoAsync($"{BaseUrl}/configuration");
            await Assertions.Expect(OtherButton("Download backup")).ToBeEnabledAsync();
            await UnlockedConfigurationPageAsync();

            await ClickUntilAsync(Button("Download backup"), Overlay);

            await Assertions.Expect(OtherStatus).ToHaveTextAsync("Backup in progress");
            await Assertions.Expect(Other.Locator(".lockout-notice")).ToHaveTextAsync(
                "A backup is under way, so the settings below can be read but not changed until it has finished.");
            await Assertions.Expect(OtherButton("Download backup")).ToBeDisabledAsync();
            await Assertions.Expect(OtherButton("Restore backup…")).ToBeDisabledAsync();
            await Assertions.Expect(Other.Locator("#max-db-size")).ToBeDisabledAsync();
            await Assertions.Expect(Other.Locator("#ping-interval")).ToBeDisabledAsync();
            await Assertions.Expect(Dialog.Locator(".step-left")).ToHaveTextAsync(new Regex(@"^4:\d\d$"));

            await Button(Dialog, "Cancel").ClickAsync();

            await Assertions.Expect(OtherStatus).ToHaveCountAsync(0);
            await Assertions.Expect(OtherButton("Unlocked")).ToBeVisibleAsync();
            await Assertions.Expect(OtherButton("Download backup")).ToBeEnabledAsync();
            await Assertions.Expect(Other.Locator("#max-db-size")).ToBeEnabledAsync();
            await Assertions.Expect(Other.Locator(".lockout-notice")).ToHaveCountAsync(0);
        }

        /// <summary>
        /// After a restore every browser has to be unlocked with the password the backup brought,
        /// even where it is the password already in force, as it is for this copy of the run's own
        /// database: the browser that restored it reads as locked once the restore is done, and so
        /// does the other, which had done nothing but stay unlocked. Both unlock with it again.
        /// </summary>
        [Test]
        public async Task Restore_LeavesEveryBrowserLockedUntilItIsUnlockedWithTheBackupsPassword()
        {
            var backup = await FaithfulCopyAsync();
            try
            {
                await UnlockOtherAsync();
                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                SqliteConnection.ClearAllPools();
                await Page.Locator("#backup-current-password").FillAsync(WebApp.Password);
                await Button(Dialog, "Continue").ClickAsync();
                var chooser = await Page.RunAndWaitForFileChooserAsync(() => Button(Dialog, "Choose file…").ClickAsync());
                await chooser.SetFilesAsync(backup);
                await Page.Locator("#backup-password").FillAsync(WebApp.Password);
                await Button(Dialog, "Restore").ClickAsync();
                await Assertions.Expect(Dialog.GetByText("Restored — the backup's hosts, history, settings and password now apply."))
                    .ToBeVisibleAsync();
                await Button(Dialog.Locator(".modal-footer"), "Close").ClickAsync();

                await Assertions.Expect(Page.GetByText("HostPinger is locked, so the settings below can be read but not changed."))
                    .ToBeVisibleAsync();
                await GoAsync("/configuration");
                await Assertions.Expect(Button("Locked")).ToBeVisibleAsync();
                await Other.GotoAsync(BaseUrl);
                await Assertions.Expect(OtherButton("Locked")).ToBeVisibleAsync();
                await Assertions.Expect(OtherButton("Add host")).ToHaveCountAsync(0);

                await OpenOverlayAsync();
                await SubmitPasswordAsync(WebApp.Password);
                await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
            }
            finally
            {
                File.Delete(backup);
            }
        }

        private ILocator OtherDialog => Other.Locator(".modal-content");

        /// <summary>What the top row of the other browser says is under way, there only while something is.</summary>
        private ILocator OtherStatus => Other.Locator(".lockout-status");

        private ILocator OtherButton(string name) =>
            Other.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = true });

        private ILocator OtherRow(string text) => Other.Locator(".host-row", new PageLocatorOptions { HasText = text });

        /// <summary>
        /// Unlocks the other browser and leaves it on the Hosts page. The unlock is a navigation, so
        /// what a test does there next is clicked until it takes, or needs no circuit at all.
        /// </summary>
        private async Task UnlockOtherAsync()
        {
            await Other.GotoAsync(BaseUrl);
            await ClickUntilAsync(OtherButton("Locked"), Other.Locator(".modal-backdrop"));
            await Other.Locator("#unlock-password").FillAsync(WebApp.Password);
            // The overlay's own: the page behind it offers an Unlock as well.
            await OtherDialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Unlock", Exact = true }).ClickAsync();
            await Assertions.Expect(OtherButton("Unlocked")).ToBeVisibleAsync();
        }

        /// <summary>The configuration page of this browser, unlocked and with a live circuit.</summary>
        private async Task UnlockedConfigurationPageAsync()
        {
            await GoAsync("/configuration");
            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
            await GoAsync("/configuration");
        }

        /// <summary>A faithful copy of the running application's database, as its own backup takes one.</summary>
        private static async Task<string> FaithfulCopyAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"hostpinger-ui-lockout-{Guid.NewGuid():N}.db");
            await using var connection = new SqliteConnection($"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync();
            return path;
        }

        /// <summary>A host that answered its one ping, written to the running application's own database.</summary>
        private static async Task SeedAsync(string name)
        {
            await using var db = new HostPingerDbContext(
                new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite($"Data Source={UiTestRun.DatabasePath}").Options);
            if (await db.Hosts.AnyAsync(h => h.Address == $"{name}.example"))
            {
                return;
            }

            var host = new MonitoredHost { Name = name, Address = $"{name}.example", CreatedUtc = DateTime.UtcNow };
            db.Hosts.Add(host);
            await UiTestRun.SaveAsync(db);
            db.PingAttempts.Add(new PingAttempt { HostId = host.Id, TimestampUtc = DateTime.UtcNow, RoundtripMs = 20 });
            await UiTestRun.SaveAsync(db);
        }
    }
}
