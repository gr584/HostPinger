using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The Hosts page keeping up with the database, both ways a change reaches it: one the page made
    /// itself, and one that lands while it is open. What the summary does in either case is settled
    /// in HostSummaryCacheTests, and what the store does with a change in HostStoreTests; what only
    /// a browser can settle is that the page is listening — that the switch and the form reach the
    /// store, that what each answers is shown where it belongs, and that what the application reads
    /// next is drawn without a reload.
    /// </summary>
    /// <remarks>
    /// Not parallel with anything, because changing a host takes an unlocked browser, and the
    /// password is checked against one tally for every fixture: LockOverlayTests runs it out on
    /// purpose, and an unlock landing while it waits would be turned away.
    /// </remarks>
    [NonParallelizable]
    public class HostsPageChangeTests : BrowserTest
    {
        /// <summary>
        /// Hosts of this fixture's own, so that nothing here changes what another test looks at, and
        /// each test's host is in the database only once the test has put it there.
        /// </summary>
        private const string ArrivingName = "change-arriving";

        private const string PausedName = "change-paused";

        /// <summary>
        /// Seeded by a test that would otherwise start on an empty table, which has no headings for
        /// <see cref="ListeningAsync"/> to click: the rows other fixtures leave are not this
        /// fixture's to count on, and run on its own it has none.
        /// </summary>
        private const string BesideName = "change-beside";

        /// <summary>
        /// A host written while the page is open, the way a round's attempts are: from outside the
        /// page, with nothing to tell it. It turns up in the table on its own.
        /// </summary>
        [Test]
        public async Task Table_TakesInAHostAddedWhileItIsOpen()
        {
            await SeedAsync(BesideName);
            await GoAsync();
            await ListeningAsync();
            await Assertions.Expect(Row(ArrivingName)).ToHaveCountAsync(0);

            await SeedAsync(ArrivingName);

            await Assertions.Expect(Status(ArrivingName)).ToHaveTextAsync("Up");
        }

        [Test]
        public async Task Switch_PausesTheHostAndResumesIt()
        {
            await SeedAsync(PausedName);
            await UnlockAsync();
            await Assertions.Expect(Status(PausedName)).ToHaveTextAsync("Up");

            await Switch(PausedName).UncheckAsync();
            await Assertions.Expect(Status(PausedName)).ToHaveTextAsync("Paused");

            await Switch(PausedName).CheckAsync();
            await Assertions.Expect(Status(PausedName)).ToHaveTextAsync("Up");
        }

        /// <summary>
        /// A pause the database refuses is reported above the table, and the switch goes back to
        /// where the host still is. It used to throw out of the page and end its circuit, so the
        /// same switch is then used again, once the database takes it, and the report goes. A
        /// trigger stands in for the refusal, for this host alone.
        /// </summary>
        [Test]
        public async Task Switch_ShowsWhyAPauseWasNotSaved()
        {
            const string name = "change-switch-refused";
            await SeedAsync(name);
            await ExecuteAsync(
                $"CREATE TRIGGER refuse_switch BEFORE UPDATE ON Hosts WHEN OLD.Name = '{name}' "
                + "BEGIN SELECT RAISE(ABORT, 'held by the test'); END;");
            try
            {
                await UnlockAsync();

                // A click rather than UncheckAsync, which would fail on the switch coming back.
                await Switch(name).ClickAsync();

                await Assertions.Expect(SwitchError)
                    .ToHaveTextAsync("The change was not saved: SQLite Error 19: 'held by the test'.");
                await Assertions.Expect(Switch(name)).ToBeCheckedAsync();
                await Assertions.Expect(Status(name)).ToHaveTextAsync("Up");

                await ExecuteAsync("DROP TRIGGER refuse_switch;");
                await Switch(name).UncheckAsync();

                await Assertions.Expect(Status(name)).ToHaveTextAsync("Paused");
                await Assertions.Expect(SwitchError).ToHaveCountAsync(0);
            }
            finally
            {
                await ExecuteAsync("DROP TRIGGER IF EXISTS refuse_switch;");
            }
        }

        /// <summary>
        /// The form adds a host, and the table it closes onto lists it under the name and address
        /// typed, never pinged yet and so waiting for its first round.
        /// </summary>
        [Test]
        public async Task Form_AddsAHost()
        {
            const string name = "change-form-added";
            await SeedAsync(BesideName);
            await UnlockAsync();

            await OpenAddFormAsync();
            await Page.Locator("#host-name").FillAsync(name);
            await Page.Locator("#host-address").FillAsync($"{name}.example");
            await Button(Dialog, "Add host").ClickAsync();

            await Assertions.Expect(Dialog).ToBeHiddenAsync();
            await Assertions.Expect(Cell(name, 0)).ToHaveTextAsync(name);
            await Assertions.Expect(Cell(name, 1)).ToHaveTextAsync($"{name}.example");
            await Assertions.Expect(Status(name)).ToHaveTextAsync("Waiting…");
        }

        /// <summary>
        /// An address another host already has is refused in the form, which stays open with what
        /// was typed so that it can be put right, and no second host appears. Typed with blanks
        /// around it, which the store trims before comparing, and the message names the address
        /// it compared.
        /// </summary>
        [Test]
        public async Task Form_RefusesAnAddressAnotherHostHas()
        {
            const string taken = "change-form-taken";
            const string second = "change-form-second";
            await SeedAsync(taken);
            await UnlockAsync();

            await OpenAddFormAsync();
            await Page.Locator("#host-name").FillAsync(second);
            await Page.Locator("#host-address").FillAsync($"  {taken}.example ");
            await Button(Dialog, "Add host").ClickAsync();

            await Assertions.Expect(Dialog.Locator(".alert"))
                .ToHaveTextAsync($"A host with address \"{taken}.example\" already exists.");
            await Assertions.Expect(Page.Locator("#host-name")).ToHaveValueAsync(second);
            await Assertions.Expect(Row(second)).ToHaveCountAsync(0);
        }

        /// <summary>
        /// The form opened from a row's Edit saves every field into that row: the name, the address
        /// and the switch.
        /// </summary>
        [Test]
        public async Task Form_EditsAHost()
        {
            const string renamed = "change-form-renamed";
            await SeedAsync("change-form-edited");
            await UnlockAsync();

            await ClickUntilAsync(Button(Row("change-form-edited"), "Edit"), Dialog);
            await Page.Locator("#host-name").FillAsync(renamed);
            await Page.Locator("#host-address").FillAsync("change-form-moved.example");
            await Page.Locator("#host-enabled").UncheckAsync();
            await Button(Dialog, "Save changes").ClickAsync();

            await Assertions.Expect(Dialog).ToBeHiddenAsync();
            await Assertions.Expect(Cell(renamed, 0)).ToHaveTextAsync(renamed);
            await Assertions.Expect(Cell(renamed, 1)).ToHaveTextAsync("change-form-moved.example");
            await Assertions.Expect(Status(renamed)).ToHaveTextAsync("Paused");
            await Assertions.Expect(Row("change-form-edited")).ToHaveCountAsync(0);
        }

        /// <summary>Deleting asks once more, and the confirmed delete takes the row away.</summary>
        [Test]
        public async Task Form_DeletesAHostOnceConfirmed()
        {
            const string name = "change-form-deleted";
            await SeedAsync(name);
            await UnlockAsync();

            await ClickUntilAsync(Button(Row(name), "Edit"), Dialog);
            await Button(Dialog, "Delete").ClickAsync();
            await Button(Dialog, "Confirm delete").ClickAsync();

            await Assertions.Expect(Dialog).ToBeHiddenAsync();
            await Assertions.Expect(Row(name)).ToHaveCountAsync(0);
        }

        /// <summary>
        /// A host deleted from elsewhere while its edit is open: the table loses the row underneath
        /// the dialog, and saving says the host is gone rather than closing as though it had saved,
        /// or bringing the host back.
        /// </summary>
        [Test]
        public async Task Form_ReportsAHostDeletedWhileItWasBeingEdited()
        {
            const string name = "change-form-vanished";
            await SeedAsync(name);
            await UnlockAsync();
            await ClickUntilAsync(Button(Row(name), "Edit"), Dialog);

            await DeleteFromOutsideAsync($"{name}.example");
            await Assertions.Expect(Row(name)).ToHaveCountAsync(0);
            await Page.Locator("#host-name").FillAsync("change-form-ghost");
            await Button(Dialog, "Save changes").ClickAsync();

            await Assertions.Expect(Dialog.Locator(".alert")).ToHaveTextAsync("This host no longer exists.");
            await Assertions.Expect(Dialog).ToBeVisibleAsync();
            await Assertions.Expect(Row("change-form-ghost")).ToHaveCountAsync(0);
        }

        /// <summary>
        /// A save the database refuses for a reason other than the address is reported with that
        /// reason in the form, which stays open with what was typed. The page used to call every
        /// such refusal a taken address. A trigger stands in for the refusal, for this host alone.
        /// </summary>
        [Test]
        public async Task Form_ShowsWhyASaveWasNotSaved()
        {
            const string name = "change-form-refused";
            await SeedAsync(BesideName);
            await ExecuteAsync(
                $"CREATE TRIGGER refuse_form_save BEFORE INSERT ON Hosts WHEN NEW.Name = '{name}' "
                + "BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;");
            try
            {
                await UnlockAsync();
                await OpenAddFormAsync();
                await Page.Locator("#host-name").FillAsync(name);
                await Page.Locator("#host-address").FillAsync($"{name}.example");
                await Button(Dialog, "Add host").ClickAsync();

                await Assertions.Expect(Dialog.Locator(".alert"))
                    .ToHaveTextAsync("The change was not saved: SQLite Error 19: 'refused by the test'.");
                await Assertions.Expect(Page.Locator("#host-name")).ToHaveValueAsync(name);
                await Assertions.Expect(Row(name)).ToHaveCountAsync(0);
            }
            finally
            {
                await ExecuteAsync("DROP TRIGGER IF EXISTS refuse_form_save;");
            }
        }

        /// <summary>
        /// A delete the database refuses is reported in the dialog, with the confirmation taken
        /// down so that the reason is not read as a question, and the host stays. It used to throw
        /// out of the page and end its circuit.
        /// </summary>
        [Test]
        public async Task Form_ShowsWhyADeleteWasNotSaved()
        {
            const string name = "change-form-undeletable";
            await SeedAsync(name);
            await ExecuteAsync(
                $"CREATE TRIGGER refuse_form_delete BEFORE DELETE ON Hosts WHEN OLD.Name = '{name}' "
                + "BEGIN SELECT RAISE(ABORT, 'kept by the test'); END;");
            try
            {
                await UnlockAsync();
                await ClickUntilAsync(Button(Row(name), "Edit"), Dialog);
                await Button(Dialog, "Delete").ClickAsync();
                await Button(Dialog, "Confirm delete").ClickAsync();

                await Assertions.Expect(Dialog.Locator(".alert"))
                    .ToHaveTextAsync("The change was not saved: SQLite Error 19: 'kept by the test'.");
                await Assertions.Expect(Button(Dialog, "Confirm delete")).ToHaveCountAsync(0);
                await Assertions.Expect(Button(Dialog, "Delete")).ToBeVisibleAsync();
                await Assertions.Expect(Row(name)).ToHaveCountAsync(1);
            }
            finally
            {
                await ExecuteAsync("DROP TRIGGER IF EXISTS refuse_form_delete;");
            }
        }

        private ILocator Row(string text) => Page.Locator(".host-row", new PageLocatorOptions { HasText = text });

        private ILocator Status(string name) => Row(name).Locator(".badge");

        /// <summary>A cell of a row, whose whole text an assertion can compare, where the row's would match a part.</summary>
        private ILocator Cell(string text, int column) => Row(text).Locator("td").Nth(column);

        private ILocator Switch(string name) => Row(name).GetByRole(AriaRole.Switch);

        private ILocator SwitchError => Page.Locator(".switch-error");

        /// <summary>
        /// Unlocks this browser and comes back to a live Hosts page, which is where every change
        /// here starts: the controls that make one are only drawn for an unlocked reader.
        /// </summary>
        private async Task UnlockAsync()
        {
            await GoAsync();
            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();

            // The unlock is a navigation, so the page it came back to needs a circuit of its own.
            await GoAsync();
            await ListeningAsync();
        }

        /// <summary>
        /// Opens the empty form. The page's button and the form's submit share the name, so the
        /// form's own is looked for inside the dialog once it is open.
        /// </summary>
        private Task OpenAddFormAsync() => ClickUntilAsync(Button("Add host"), Dialog);

        /// <summary>
        /// Waits until the page is listening, which a switch cannot be used to find out: unlike the
        /// buttons ClickUntilAsync is for, a click the page did not hear still moves a switch in the
        /// browser, and a second click to make sure moves it back. A heading can be clicked until it
        /// takes, and once it has sorted the table, the page is attached to its circuit.
        /// </summary>
        private Task ListeningAsync() => ClickUntilAsync(Button("Name"), Page.Locator("th[aria-sort='descending']"));

        /// <summary>Deletes a host from outside the page, the way another browser would.</summary>
        private static async Task DeleteFromOutsideAsync(string address)
        {
            await using var db = new HostPingerDbContext(AppDatabase());
            db.Hosts.Remove(await db.Hosts.SingleAsync(h => h.Address == address));
            await UiTestRun.SaveAsync(db);
        }

        /// <summary>Runs a statement on the running application's database, for a trigger a test needs.</summary>
        private static async Task ExecuteAsync(string sql)
        {
            await using var db = new HostPingerDbContext(AppDatabase());
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        private static DbContextOptions<HostPingerDbContext> AppDatabase() =>
            new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={UiTestRun.DatabasePath}")
                .Options;

        /// <summary>
        /// Writes to the running application's own database, the way HostsPageTests does: a host
        /// that answered its one ping, so that it reads as up while it is being pinged. The address
        /// does not resolve, so the rounds running alongside this record their failure to look it up
        /// and never add an attempt of their own to change what it reads as.
        /// </summary>
        private static async Task SeedAsync(string name)
        {
            await using var db = new HostPingerDbContext(AppDatabase());

            // Some hosts are seeded by more than one test; the first one's is the one they share.
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
