using HostPinger.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The Configuration page against a real instance: the figures it reads about the database,
    /// and backup and restore, driven the way a person drives them. What the figures are is settled
    /// in DatabaseStatsTests and DatabaseReadsTests, and what <c>DatabaseBackup</c> does to files
    /// in DatabaseBackupTests; what only a browser can settle is that the page shows a figure it
    /// actually read, and the transport around backup and restore — that both ask for the current
    /// password, that the download button hands over a database, and that the file
    /// chosen for a restore survives into the confirmation step. That last one is a regression
    /// test: the browser keeps the chosen file on the input element, and a confirm strip that
    /// replaced the input in the DOM lost the upload before it could be read.
    /// </summary>
    /// <remarks>
    /// Not parallel with anything, because the restore swaps the database file under the one
    /// application every fixture shares. The swap puts back a faithful copy of what was there,
    /// so the others find the world unchanged afterwards — but not while it happens.
    /// </remarks>
    [NonParallelizable]
    public class ConfigurationPageTests : BrowserTest
    {
        /// <summary>
        /// The size under the database settings is the size of the database the application is
        /// running on, read the way the page reads it, rather than the dash the page shows until a
        /// read has landed. Nothing writes while this fixture runs, so the two agree to the byte.
        /// Formatted in the same culture as the application, which this run passes on to it.
        /// </summary>
        [Test]
        public async Task Page_ShowsTheSizeOfTheDatabaseItRead()
        {
            long bytes;
            await using (var connection = new SqliteConnection($"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();";
                bytes = Convert.ToInt64(await command.ExecuteScalarAsync());
            }

            await GoAsync("/configuration");

            await Assertions.Expect(Page.Locator(".config-value").First).ToHaveTextAsync(FormatBytes(bytes));
        }

        /// <summary>
        /// The capacity is projected against the settings in force rather than the defaults, so with
        /// the size limit saved as 0 the page says pruning is off. A page projecting against the
        /// default settings instead — the one change a mutation pass found no test could see, since
        /// this database holds too few attempts for any projection — says there is not enough data
        /// yet, the answer for the default 1024 MB.
        /// </summary>
        [Test]
        public async Task Capacity_FollowsTheSizeLimitInForce()
        {
            await UnlockedConfigurationPageAsync();
            var size = Page.Locator("#max-db-size");
            var save = Page.Locator(".input-group", new PageLocatorOptions { Has = size })
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" });
            var unlimited = Page.GetByText("Unlimited — pruning is disabled.");
            await Assertions.Expect(size).ToBeEnabledAsync();
            var original = await size.InputValueAsync();
            try
            {
                await size.FillAsync("0");
                await save.ClickAsync();

                await Assertions.Expect(unlimited).ToBeVisibleAsync();
            }
            finally
            {
                await size.FillAsync(original);
                await save.ClickAsync();
                await Assertions.Expect(unlimited).ToHaveCountAsync(0);
            }
        }

        /// <summary>The page's way of writing a size, which the assertion above has to match exactly.</summary>
        private static string FormatBytes(double bytes)
        {
            string[] units = ["bytes", "KB", "MB", "GB", "TB"];
            var unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1)
            {
                bytes /= 1024;
                unit++;
            }

            return $"{bytes:0.#} {units[unit]}";
        }

        /// <summary>
        /// The download asks for the current password in an overlay, even in an unlocked browser,
        /// because the file carries its hash. The overlay opens on its password box, an empty box
        /// and a wrong password are each turned away inside it with their own sentence and the box
        /// emptied, Escape closes it, and the right password shows the snapshot being written in a
        /// progress bar, then hands over a database without navigating the page.
        /// </summary>
        [Test]
        public async Task Backup_DownloadsAnSqliteDatabaseOnlyForTheCurrentPassword()
        {
            await UnlockedConfigurationPageAsync();
            await ClickUntilAsync(Button("Download backup"), Overlay);
            await Assertions.Expect(CurrentPassword).ToBeFocusedAsync();

            await Button(Dialog, "Download").ClickAsync();
            await Assertions.Expect(Dialog.GetByText("Enter the current password.")).ToBeVisibleAsync();

            await CurrentPassword.FillAsync("not the password");
            await Button(Dialog, "Download").ClickAsync();
            await Assertions.Expect(Dialog.GetByText("That is not the current password.")).ToBeVisibleAsync();
            await Assertions.Expect(CurrentPassword).ToHaveValueAsync(string.Empty);

            await CurrentPassword.PressAsync("Escape");
            await Assertions.Expect(Overlay).ToBeHiddenAsync();

            // Which also clears the wrong one above off the tally the rest of the run shares.
            await Button("Download backup").ClickAsync();
            await CurrentPassword.FillAsync(WebApp.Password);
            await WatchProgressAsync();

            // Firefox closes the circuit's connection as soon as the page starts to navigate, even
            // to an attachment, and the circuit ends a minute later. Chromium, which drives these
            // tests, keeps the connection, but tells the page it is being left just the same.
            await Page.EvaluateAsync("() => addEventListener('beforeunload', () => window.leftThePage = true)");
            var download = await Page.RunAndWaitForDownloadAsync(() => Button(Dialog, "Download").ClickAsync());
            await Assertions.Expect(Overlay).ToBeHiddenAsync();
            Assert.That(await Page.EvaluateAsync<bool>("() => window.leftThePage === true"), Is.False,
                "the download navigated the page instead of starting from a download link");

            // The test's database is copied in a moment, so the bar is seen at the start and the
            // end, and rarely in between.
            var steps = await ProgressStepsAsync();
            Assert.Multiple(() =>
            {
                Assert.That(steps, Is.Not.Empty);
                Assert.That(steps.Select(s => s.Label), Has.All.EqualTo("Preparing the backup…"));
                Assert.That(steps.Select(s => s.Percent), Has.All.InRange(0, 100));
                Assert.That(steps.Select(s => s.Percent), Is.Ordered);
                Assert.That(steps[^1].Percent, Is.EqualTo(100), "done before the download started");
            });

            Assert.That(download.SuggestedFilename, Does.StartWith("hostpinger-backup-").And.EndWith(".db"));

            var header = new byte[16];
            await using (var stream = File.OpenRead((await download.PathAsync())!))
            {
                stream.ReadExactly(header);
            }

            Assert.That(header, Is.EqualTo("SQLite format 3\0"u8.ToArray()),
                "what the browser saves must open as an SQLite database");
        }

        /// <summary>
        /// The download's address is no use away from the unlocked browser it was made for. The
        /// page writes the snapshot before the browser asks for it, so a refusal has a file to
        /// deal with: asked for without the unlock cookie, the address is NotFound, the snapshot is
        /// deleted rather than left beside the database, and the token is spent, so the browser it
        /// was made for is turned away with it too.
        /// </summary>
        [Test]
        public async Task Backup_AddressIsRefusedWithoutTheUnlockAndTheSnapshotDeleted()
        {
            await UnlockedConfigurationPageAsync();

            // The page starts the download by clicking a link of its own, which a route does not
            // see. Held back here instead, so the address is still unspent.
            await Page.EvaluateAsync(@"() => document.addEventListener('click', e => {
                const link = e.target.closest('a[download]');
                if (link) {
                    e.preventDefault();
                    window.downloadAddress = link.href;
                }
            }, { capture: true })");

            await ClickUntilAsync(Button("Download backup"), Overlay);
            await CurrentPassword.FillAsync(WebApp.Password);
            await Button(Dialog, "Download").ClickAsync();
            var address = await (await Page.WaitForFunctionAsync("() => window.downloadAddress")).JsonValueAsync<string>();

            var snapshots = () => Directory.EnumerateFiles(
                Path.GetDirectoryName(UiTestRun.DatabasePath)!, "hostpinger-backup-*");
            Assert.That(snapshots(), Is.Not.Empty, "written, and waiting for the browser");

            await using var stranger = await UiTestRun.Browser.NewContextAsync();
            var refused = await stranger.APIRequest.GetAsync(address);
            var afterwards = await Page.APIRequest.GetAsync(address);

            Assert.Multiple(() =>
            {
                Assert.That(refused.Status, Is.EqualTo(404));
                Assert.That(snapshots(), Is.Empty);
                Assert.That(afterwards.Status, Is.EqualTo(404), "spent");
            });
        }

        /// <summary>
        /// A restore asks for the current password before anything else, because what it installs
        /// is the backup's: a wrong one is turned away before a file is even chosen, and the right
        /// one leads on to choosing it. The chosen file survives into the confirmation, and
        /// confirming restores it, with a progress bar through each step, and says so in the
        /// overlay, which stays until it is closed.
        /// </summary>
        [Test]
        public async Task Restore_AsksForTheCurrentPasswordFirstAndRestoresTheFileChosenAfter()
        {
            // A faithful copy of the running application's database, taken the way its own
            // backup takes one, so the restore changes nothing the other fixtures depend on.
            var uploadPath = Path.Combine(Path.GetTempPath(), $"hostpinger-ui-upload-{Guid.NewGuid():N}.db");
            try
            {
                await using (var connection = new SqliteConnection(
                    $"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM INTO $path;";
                    command.Parameters.AddWithValue("$path", uploadPath);
                    await command.ExecuteNonQueryAsync();
                }

                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await Assertions.Expect(CurrentPassword).ToBeFocusedAsync();

                await CurrentPassword.FillAsync("not the password");
                await Button(Dialog, "Continue").ClickAsync();
                await Assertions.Expect(Dialog.GetByText("That is not the current password.")).ToBeVisibleAsync();
                await Assertions.Expect(CurrentPassword).ToHaveValueAsync(string.Empty);
                await Assertions.Expect(Button(Dialog, "Choose file…")).ToHaveCountAsync(0);

                await GiveTheRestorePasswordAndChooseAsync(uploadPath);

                // This process holds the file open too: whatever the fixtures seeded through sits in
                // its connection pool, still attached to the file the restore is about to retire,
                // and a seed made afterwards would be written there, where the application never
                // looks. Let go of it first, so every later connection opens the restored file.
                // First rather than after, because a connection to a WAL database that closes last
                // deletes the -wal file by name, and after the swap that name is the new file's.
                SqliteConnection.ClearAllPools();
                await WatchProgressAsync();
                await Button("Confirm restore").ClickAsync();

                await Assertions.Expect(Dialog.GetByText(
                        "Restored — the backup's hosts, history, settings and password now apply."))
                    .ToBeVisibleAsync();
                await Assertions.Expect(Button(Dialog, "Cancel")).ToHaveCountAsync(0);
                await AssertRestoreProgressAsync();
                Assert.That(File.Exists(UiTestRun.DatabasePath + ".pre-restore"),
                    "the database that was replaced must be kept as the undo copy");

                // The footer's, since the header's cross is called Close too.
                await Button(Dialog.Locator(".modal-footer"), "Close").ClickAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// A backup that passes validation but will not open — here a faithful copy missing one of
        /// its tables — is reported in the overlay, with the page still working, and the
        /// database it was meant to replace is the one the application runs on afterwards. Before,
        /// the failure escaped the page's catch and ended the circuit, and the half-restored file
        /// stayed in place; now it is found out before the swap, and nothing is replaced.
        /// </summary>
        [Test]
        public async Task Restore_ReportsABackupThatWillNotOpenAndKeepsTheDatabase()
        {
            var uploadPath = Path.Combine(Path.GetTempPath(), $"hostpinger-ui-upload-{Guid.NewGuid():N}.db");
            try
            {
                long hosts;
                await using (var connection = new SqliteConnection(
                    $"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM INTO $path;";
                    command.Parameters.AddWithValue("$path", uploadPath);
                    await command.ExecuteNonQueryAsync();
                    command.CommandText = "SELECT COUNT(*) FROM Hosts;";
                    command.Parameters.Clear();
                    hosts = Convert.ToInt64(await command.ExecuteScalarAsync());
                }

                // Padded out too, so the upload takes long enough for the bar to be seen partway.
                // The file is refused, so the padding never reaches the database the run shares.
                await using (var connection = new SqliteConnection($"Data Source={uploadPath};Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DROP TABLE ResolverErrors;"
                        + "CREATE TABLE Padding (Data BLOB);"
                        + "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000) "
                        + "INSERT INTO Padding SELECT randomblob(4000) FROM n;";
                    await command.ExecuteNonQueryAsync();
                }

                await UnlockedConfigurationPageAsync();

                // Chosen and confirmed the same way, and for the same reasons, as the restore above.
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await GiveTheRestorePasswordAndChooseAsync(uploadPath);
                SqliteConnection.ClearAllPools();
                await WatchProgressAsync();
                await Button("Confirm restore").ClickAsync();

                await Assertions.Expect(Dialog.GetByText(
                        "The restore failed: The backup has no ResolverErrors table. Nothing was replaced."))
                    .ToBeVisibleAsync();
                var uploaded = await AssertRestoreProgressAsync();
                Assert.That(uploaded, Has.Some.InRange(1, 99), "the bar followed the upload partway");
                await Assertions.Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();

                // The confirm button the keyboard was on is gone, so Escape only reaches the
                // overlay if it has been handed the focus.
                await Assertions.Expect(Page.Locator(".modal")).ToBeFocusedAsync();
                await Page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(Overlay).ToBeHiddenAsync();

                await using var check = new SqliteConnection($"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False");
                await check.OpenAsync();
                await using var query = check.CreateCommand();
                query.CommandText = "SELECT (SELECT COUNT(*) FROM Hosts), (SELECT COUNT(*) FROM sqlite_master WHERE name = 'ResolverErrors');";
                await using var reader = await query.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert.Multiple(() =>
                {
                    Assert.That(reader.GetInt64(0), Is.EqualTo(hosts), "the application runs on the database it had");
                    Assert.That(reader.GetInt64(1), Is.EqualTo(1), "with every table it had");
                });
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// The overlay asks for the password before the file, but that order is only what the page
        /// offers: anyone with the browser's developer tools can put a file on the page's hidden
        /// input directly, which opens the confirmation with no password given. Confirming then
        /// asks for the password instead of restoring, and once it is given comes straight back to
        /// the same file. A password given for an earlier restore, since cancelled, does not count.
        /// </summary>
        [Test]
        public async Task Restore_ConfirmedWithoutThePasswordAsksForItInstead()
        {
            // A faithful copy, as above, in case it restores after all.
            var uploadPath = Path.Combine(Path.GetTempPath(), $"hostpinger-ui-upload-{Guid.NewGuid():N}.db");
            try
            {
                await using (var connection = new SqliteConnection(
                    $"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM INTO $path;";
                    command.Parameters.AddWithValue("$path", uploadPath);
                    await command.ExecuteNonQueryAsync();
                }

                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await CurrentPassword.FillAsync(WebApp.Password);
                await Button(Dialog, "Continue").ClickAsync();
                await Assertions.Expect(Button(Dialog, "Choose file…")).ToBeVisibleAsync();
                await Button(Dialog, "Cancel").ClickAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();

                await Page.Locator("#restore-file").SetInputFilesAsync(uploadPath);
                var confirm = Button("Confirm restore");
                await Assertions.Expect(confirm).ToBeVisibleAsync();
                SqliteConnection.ClearAllPools();
                await confirm.ClickAsync();
                await Assertions.Expect(CurrentPassword).ToBeFocusedAsync();

                await CurrentPassword.FillAsync(WebApp.Password);
                await Button(Dialog, "Continue").ClickAsync();
                await Assertions.Expect(confirm).ToBeVisibleAsync();
                await Assertions.Expect(Dialog.Locator("code").First).ToHaveTextAsync(Path.GetFileName(uploadPath));

                await Button(Dialog, "Cancel").ClickAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();
                await Assertions.Expect(Page.GetByText("Restored —")).ToHaveCountAsync(0);
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// A wrong current password typed in this page's overlay is a guess like any other. Once
        /// the schedule's free ones are spent, the next earns a wait there, and the overlay holds
        /// its button back. It is also counted on the same tally as the unlock overlay's, so the
        /// unlock overlay's next wrong answer is turned away with a wait too, where a tally of its
        /// own would have let it go free. That is what stops this being a way round the unlock
        /// overlay's wait, and it shows the page knows the address its clicks come from, which a
        /// circuit is not handed the way a request is.
        /// </summary>
        /// <remarks>
        /// The whole run reaches the application from one address, and nothing else runs while
        /// this fixture does, which is what makes earning a wait here safe. The test sits it out
        /// and unlocks afterwards, which clears the tally for whatever runs next.
        /// </remarks>
        [Test]
        public async Task CurrentPassword_WaitsOnTheSameTallyAsTheUnlockOverlay()
        {
            await UnlockedConfigurationPageAsync();
            await ClickUntilAsync(Button("Download backup"), Overlay);

            var free = 0;
            while (PasswordAttempts.WaitFor(free + 1) == TimeSpan.Zero)
            {
                free++;
            }

            var download = Button(Dialog, "Download");
            for (var attempt = 0; attempt < free; attempt++)
            {
                await CurrentPassword.FillAsync("not the password");
                await download.ClickAsync();

                // Emptied once the attempt has been looked at, which is the round trip to wait
                // for before the next one: the sentence above it reads the same each time.
                await Assertions.Expect(CurrentPassword).ToHaveValueAsync(string.Empty);
                await Assertions.Expect(Dialog.GetByText("That is not the current password.")).ToBeVisibleAsync();
            }

            await CurrentPassword.FillAsync("not the password");
            await download.ClickAsync();
            await Assertions.Expect(Dialog.GetByText("Too many wrong passwords")).ToBeVisibleAsync();
            await Assertions.Expect(download).ToBeDisabledAsync();

            await CurrentPassword.PressAsync("Escape");
            await Assertions.Expect(Overlay).ToBeHiddenAsync();
            await ClickUntilAsync(Button("Unlocked"), Overlay);
            await Button(Dialog, "Lock").ClickAsync();
            await OpenOverlayAsync();

            // Turned away whether it arrives inside the wait just earned or after it, which then
            // earns the next: a tally of its own would have let it go free, and said so instead.
            await SubmitPasswordAsync("not the password");
            await Assertions.Expect(Dialog.GetByText("Too many wrong passwords")).ToBeVisibleAsync();

            var timeout = new LocatorAssertionsToBeVisibleOptions { Timeout = 90_000 };
            await Assertions.Expect(Dialog.GetByText("You can try again now.")).ToBeVisibleAsync(timeout);
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
        }

        /// <summary>
        /// Starts noting each state the overlay's progress bar is drawn in: its label, and its
        /// percentage or null for a bar that moves without one. A step can come and go between two
        /// of Playwright's looks, so the page watches itself, and sees every change the circuit
        /// sends as it is applied.
        /// </summary>
        private Task WatchProgressAsync() => Page.EvaluateAsync("""
            () => {
                window.progressSeen = [];
                const note = () => {
                    const bar = document.querySelector('.modal .progress');
                    const seen = bar && [bar.getAttribute('aria-label'), bar.getAttribute('aria-valuenow')];
                    const last = window.progressSeen.at(-1);
                    if (seen && (!last || last[0] !== seen[0] || last[1] !== seen[1])) {
                        window.progressSeen.push(seen);
                    }
                };
                new MutationObserver(note).observe(document.body,
                    { subtree: true, childList: true, attributes: true, characterData: true });
            }
            """);

        /// <summary>What <see cref="WatchProgressAsync"/> has seen so far, in order, repeats left out.</summary>
        private async Task<List<(string Label, int? Percent)>> ProgressStepsAsync()
        {
            var seen = await Page.EvaluateAsync<string?[][]>("() => window.progressSeen");
            return seen.Select(s => (s[0]!, s[1] is { } percent ? int.Parse(percent) : (int?)null)).ToList();
        }

        /// <summary>
        /// That a restore's bar went through its three steps in order: the upload by percentage to
        /// 100, then the check and the swap without one. Returns the upload's percentages.
        /// </summary>
        private async Task<List<int>> AssertRestoreProgressAsync()
        {
            var steps = await ProgressStepsAsync();
            var uploaded = steps.Where(s => s.Label == "Uploading the backup…").Select(s => s.Percent ?? -1).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(steps.Select(s => s.Label).Distinct(), Is.EqualTo(new[]
                {
                    "Uploading the backup…", "Checking the backup…", "Putting the backup in place…",
                }));
                Assert.That(uploaded, Is.Ordered.And.Not.Empty);
                Assert.That(uploaded[^1], Is.EqualTo(100));
                Assert.That(steps.Where(s => s.Label != "Uploading the backup…").Select(s => s.Percent), Has.All.Null);
            });

            return uploaded;
        }

        /// <summary>The password box in the overlay that confirms a download or a restore.</summary>
        private ILocator CurrentPassword => Page.Locator("#backup-current-password");

        /// <summary>
        /// Gives the right password to the restore overlay, open on its first step, then chooses
        /// <paramref name="path"/> from the step after it and waits for the confirmation. The
        /// overlay was opened over the circuit, so nothing here can run ahead of it.
        /// </summary>
        private async Task GiveTheRestorePasswordAndChooseAsync(string path)
        {
            await CurrentPassword.FillAsync(WebApp.Password);
            await Button(Dialog, "Continue").ClickAsync();
            var chooser = await Page.RunAndWaitForFileChooserAsync(() => Button(Dialog, "Choose file…").ClickAsync());
            await chooser.SetFilesAsync(path);
            await Assertions.Expect(Button("Confirm restore")).ToBeVisibleAsync();
        }

        /// <summary>
        /// The configuration page, unlocked and with a live circuit. The unlock is a navigation,
        /// so the circuit the first <see cref="BrowserTest.GoAsync"/> waited for is gone by the
        /// time it lands; going to the page again is what waits for its replacement.
        /// </summary>
        private async Task UnlockedConfigurationPageAsync()
        {
            await GoAsync("/configuration");
            await OpenOverlayAsync();
            await SubmitPasswordAsync(WebApp.Password);
            await Assertions.Expect(Button("Unlocked")).ToBeVisibleAsync();
            await GoAsync("/configuration");
        }
    }
}
