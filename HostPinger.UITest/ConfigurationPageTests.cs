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
        /// The capacity is projected against the settings in force rather than the defaults, and
        /// says which limit it projects against, so with the size limit saved as 5 MB the page names
        /// 5 MB. A page projecting against the default settings instead — the one change a mutation
        /// pass found no test could see, since this database holds too few attempts for any
        /// projection — names the default's 1 GB.
        /// </summary>
        [Test]
        public async Task Capacity_FollowsTheSizeLimitInForce()
        {
            await UnlockedConfigurationPageAsync();
            var (size, save) = (MaximumSize, SaveMaximumSize);
            var fiveMb = Page.GetByText("Not enough data yet to say how long 5 MB will last.");
            await Assertions.Expect(size).ToBeEnabledAsync();
            var original = await size.InputValueAsync();
            try
            {
                await size.FillAsync("5");
                await save.ClickAsync();

                await Assertions.Expect(fiveMb).ToBeVisibleAsync();
            }
            finally
            {
                await size.FillAsync(original);
                await save.ClickAsync();
                await Assertions.Expect(fiveMb).ToHaveCountAsync(0);
            }
        }

        /// <summary>
        /// The page says how large a limit this disk can safely take, and a save beyond it is
        /// refused, saying the same, with the limit in force left as it was. A limit of 0, which
        /// used to turn pruning off, is refused before it is sent.
        /// </summary>
        [Test]
        public async Task MaximumSize_ShowsTheSafeLimitAndRefusesMore()
        {
            await UnlockedConfigurationPageAsync();
            var (size, save) = (MaximumSize, SaveMaximumSize);
            await Assertions.Expect(size).ToBeEnabledAsync();
            var original = await size.InputValueAsync();
            var hint = Page.Locator(".safe-limit");
            await Assertions.Expect(hint).ToContainTextAsync("is safe on this disk");

            // Rendered with the hint, from the same read, so its absence is not a read still to come.
            await Assertions.Expect(Page.Locator(".nearly-full")).ToHaveCountAsync(0);
            var safe = (await hint.InnerTextAsync()).Split(" MB")[0].Replace("At most", string.Empty).Trim();
            try
            {
                await size.FillAsync("0");
                await save.ClickAsync();
                await Assertions.Expect(Page.GetByText("Enter a size between 1 and 1,000,000 MB.")).ToBeVisibleAsync();

                await size.FillAsync("1000000");
                await save.ClickAsync();
                await Assertions.Expect(Page.GetByText($"This disk can safely hold {safe} MB at most")).ToBeVisibleAsync();

                // Read back the way a page opened afresh reads it: from the settings in force.
                await GoAsync("/configuration");
                await Assertions.Expect(MaximumSize).ToHaveValueAsync(original);
            }
            finally
            {
                // Still unlocked: the unlock is the browser's, and outlasts the page.
                await GoAsync("/configuration");
                await MaximumSize.FillAsync(original);
                await SaveMaximumSize.ClickAsync();
                await Assertions.Expect(Page.GetByText("Saved — applied after the next ping round.")).ToBeVisibleAsync();
            }
        }

        /// <summary>The database section's size limit box, and the Save beside it.</summary>
        private ILocator MaximumSize => Page.Locator("#max-db-size");

        private ILocator SaveMaximumSize => Page.Locator(".input-group", new PageLocatorOptions { Has = MaximumSize })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save" });

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
            var (labels, percents) = await ProgressSeenAsync();
            Assert.Multiple(() =>
            {
                Assert.That(labels, Is.EqualTo(new[] { "Preparing the backup…" }));
                Assert.That(percents, Has.All.InRange(0, 100));
                Assert.That(percents, Is.Ordered);
                Assert.That(percents[^1], Is.EqualTo(100), "done before the download started");
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
        /// one leads on to choosing it. The chosen file survives into the confirmation, confirming
        /// it uploads and checks it, and the backup's password then restores it, with a progress bar
        /// through each step, and says so in the overlay, which stays until it is closed.
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
                // its connection pool, still attached to the file the restore is about to replace,
                // and a seed made afterwards would be written there, where the application never
                // looks. Let go of it first, so every later connection opens the restored file.
                // First rather than after, because a connection to a WAL database that closes last
                // deletes the -wal file by name, and after the swap that name is the new file's.
                SqliteConnection.ClearAllPools();
                await WatchProgressAsync();
                await Button("Confirm restore").ClickAsync();
                await GiveTheBackupPasswordAsync(WebApp.Password);

                await Assertions.Expect(Dialog.GetByText(
                        "Restored — the backup's hosts, history, settings and password now apply."))
                    .ToBeVisibleAsync();
                await Assertions.Expect(Button(Dialog, "Cancel")).ToHaveCountAsync(0);
                await AssertRestoreProgressAsync();
                Assert.That(File.Exists(UiTestRun.DatabasePath + ".pre-restore"), Is.False,
                    "no copy of the database that was replaced is kept");

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
                await GiveTheBackupPasswordAsync(WebApp.Password);

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
        /// The backup's password is asked for after the upload and its check, in a step of its own.
        /// An empty box goes no further, and a wrong answer brings the step back, saying so above an
        /// empty box with the keyboard in it, as often as it takes. The bar shows the file was
        /// uploaded and checked the once, whatever was answered. Each wrong answer is logged with
        /// the address it came from, which the empty box is not.
        /// </summary>
        [Test]
        public async Task Restore_AsksAgainForAWrongBackupPasswordWithoutUploadingAgain()
        {
            // A faithful copy, as above, since it is restored in the end; its password is the run's.
            var uploadPath = await FaithfulCopyAsync();
            try
            {
                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await GiveTheRestorePasswordAndChooseAsync(uploadPath);
                await Assertions.Expect(BackupPassword).ToHaveCountAsync(0);

                const string WrongAnswer = "Failed restore from 127.0.0.1: the backup's password was wrong.";
                var loggedBefore = UiTestRun.LinesLogged(WrongAnswer);

                SqliteConnection.ClearAllPools();
                await WatchProgressAsync();
                await Button("Confirm restore").ClickAsync();
                await Assertions.Expect(BackupPassword).ToBeFocusedAsync();

                await Button(Dialog, "Restore").ClickAsync();
                await Assertions.Expect(Dialog.GetByText("Enter the backup's password.")).ToBeVisibleAsync();
                await Assertions.Expect(BackupPassword).ToBeFocusedAsync();

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await BackupPassword.FillAsync("not the backup's password");
                    await Button(Dialog, "Restore").ClickAsync();
                    await Assertions.Expect(Dialog.GetByText("That is not the backup's password.")).ToBeVisibleAsync();
                    await Assertions.Expect(BackupPassword).ToHaveValueAsync(string.Empty);
                    await Assertions.Expect(BackupPassword).ToBeFocusedAsync();
                }

                // By the keyboard this time, which is how a typed password is usually sent.
                await BackupPassword.FillAsync(WebApp.Password);
                await BackupPassword.PressAsync("Enter");
                await Assertions.Expect(Dialog.GetByText(
                        "Restored — the backup's hosts, history, settings and password now apply."))
                    .ToBeVisibleAsync();

                var (labels, _) = await ProgressSeenAsync();
                Assert.That(labels, Is.EqualTo(new[]
                {
                    "Uploading the backup…", "Checking the backup…", "Checking the password…", "Putting the backup in place…",
                }));
                Assert.That(() => UiTestRun.LinesLogged(WrongAnswer) - loggedBefore, Is.EqualTo(2).After(5000, 50));

                await Button(Dialog.Locator(".modal-footer"), "Close").ClickAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// Somebody who cannot give the backup's password can leave, with Cancel or Escape, after any
        /// number of wrong answers. The application stays on the database it had, and nothing of the
        /// upload is kept. A click away is not leaving, since it would throw away an upload that may
        /// have taken a while. The upload held the turn backups and restores share, and leaving gives
        /// it back, so the backup a person might want next is not turned away.
        /// </summary>
        [TestCase("Cancel")]
        [TestCase("Escape")]
        public async Task Restore_CanBeLeftAtTheBackupsPasswordWithNothingReplaced(string leavingBy)
        {
            // A copy with a setting of its own, which would show had it been restored.
            var uploadPath = await FaithfulCopyAsync();
            try
            {
                await using (var connection = new SqliteConnection($"Data Source={uploadPath};Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO UserSettings (Key, Value) VALUES ('Pinger:RetryAttempts', '77') "
                        + "ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;";
                    await command.ExecuteNonQueryAsync();
                }

                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await GiveTheRestorePasswordAndChooseAsync(uploadPath);
                SqliteConnection.ClearAllPools();
                await Button("Confirm restore").ClickAsync();
                await GiveTheBackupPasswordAsync("not the backup's password");
                await Assertions.Expect(Dialog.GetByText("That is not the backup's password.")).ToBeVisibleAsync();

                // Nothing on screen marks a click that is ignored, so it gets a round trip first.
                await Page.Mouse.ClickAsync(20, 20);
                await Page.WaitForTimeoutAsync(1000);
                await Assertions.Expect(BackupPassword).ToBeVisibleAsync();

                if (leavingBy == "Cancel")
                {
                    await Button(Dialog, "Cancel").ClickAsync();
                }
                else
                {
                    await BackupPassword.PressAsync("Escape");
                }

                await Assertions.Expect(Overlay).ToBeHiddenAsync();
                await Assertions.Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();

                await ClickUntilAsync(Button("Download backup"), Overlay);
                await CurrentPassword.FillAsync(WebApp.Password);
                var download = await Page.RunAndWaitForDownloadAsync(() => Button(Dialog, "Download").ClickAsync());
                Assert.That(download.SuggestedFilename, Does.StartWith("hostpinger-backup-"));

                await using var check = new SqliteConnection($"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False");
                await check.OpenAsync();
                await using var query = check.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM UserSettings WHERE Key = 'Pinger:RetryAttempts' AND Value = '77';";
                var uploadsSetting = Convert.ToInt64(await query.ExecuteScalarAsync());
                var uploads = Directory.EnumerateFiles(Path.GetDirectoryName(UiTestRun.DatabasePath)!, "hostpinger-restore-*");
                Assert.Multiple(() =>
                {
                    Assert.That(uploadsSetting, Is.Zero, "the application runs on the database it had");
                    Assert.That(uploads, Is.Empty, "nothing of the upload is kept");
                });
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// The step can stay open for minutes, and a password changed from elsewhere meanwhile locks
        /// this browser. An answer from it then replaces nothing, and is told why, and the upload
        /// waiting for that answer is let go of. Here the elsewhere is another tab, which is signed
        /// in again by its own change where the page already open is not.
        /// </summary>
        [Test]
        public async Task Restore_AnsweredFromABrowserLockedMeanwhileReplacesNothing()
        {
            const string Changed = "a password changed from elsewhere";
            var uploadPath = await FaithfulCopyAsync();
            var otherTab = await Page.Context.NewPageAsync();
            var changed = false;
            try
            {
                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await GiveTheRestorePasswordAndChooseAsync(uploadPath);
                SqliteConnection.ClearAllPools();
                await Button("Confirm restore").ClickAsync();
                await Assertions.Expect(BackupPassword).ToBeFocusedAsync();

                await ChangePasswordAsync(otherTab, WebApp.Password, Changed);
                changed = true;
                await GiveTheBackupPasswordAsync(WebApp.Password);

                await Assertions.Expect(Page.GetByText("HostPinger is locked — unlock it to restore a backup."))
                    .ToBeVisibleAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();
                var directory = Path.GetDirectoryName(UiTestRun.DatabasePath)!;
                Assert.That(() => Directory.EnumerateFiles(directory, "hostpinger-restore-*"), Is.Empty.After(5000, 50));
            }
            finally
            {
                if (changed)
                {
                    await ChangePasswordAsync(otherTab, Changed, WebApp.Password);
                }

                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// A press that reaches the application before the render that disabled its button, as one
        /// can over any distance, is ignored. A second press of the confirmation does not start a
        /// second restore, which the first would turn away, and a Cancel right behind the answer to
        /// the backup's password does not close the overlay on a restore that goes through anyway.
        /// </summary>
        [Test]
        public async Task Restore_IgnoresAPressThatRacesTheOneBeforeIt()
        {
            // A faithful copy, as above, since it is restored; its password is the run's.
            var uploadPath = await FaithfulCopyAsync();
            try
            {
                await UnlockedConfigurationPageAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await GiveTheRestorePasswordAndChooseAsync(uploadPath);

                SqliteConnection.ClearAllPools();
                await WatchTheOverlayAsync();
                await PressInOneGoAsync("Confirm restore", "Confirm restore");
                await BackupPassword.FillAsync(WebApp.Password);
                await PressInOneGoAsync("Restore", "Cancel");
                await Assertions.Expect(Dialog.GetByText(
                        "Restored — the backup's hosts, history, settings and password now apply."))
                    .ToBeVisibleAsync();

                var turnedAway = await Page.EvaluateAsync<bool>("() => window.overlaySeen.turnedAway");
                var closed = await Page.EvaluateAsync<bool>("() => window.overlaySeen.closed");
                Assert.Multiple(() =>
                {
                    Assert.That(turnedAway, Is.False, "the second press started nothing");
                    Assert.That(closed, Is.False, "the Cancel left the overlay as it was");
                });

                await Button(Dialog.Locator(".modal-footer"), "Close").ClickAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// A backup whose size limit is more than this disk can safely hold is restored with the
        /// limit lowered, and the overlay says what it was and what it is now. The run's own limit
        /// is put back afterwards, so the fixtures after this one find the world as it was.
        /// </summary>
        [Test]
        public async Task Restore_SaysWhenItLoweredTheBackupsSizeLimit()
        {
            // A faithful copy, as above, with a limit no disk this runs on can hold.
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

                await using (var connection = new SqliteConnection($"Data Source={uploadPath};Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO UserSettings (Key, Value) VALUES ('Pinger:MaxDatabaseSizeMb', '1000000') "
                        + "ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;";
                    await command.ExecuteNonQueryAsync();
                }

                await UnlockedConfigurationPageAsync();
                await Assertions.Expect(MaximumSize).ToBeEnabledAsync();
                var original = await MaximumSize.InputValueAsync();
                await ClickUntilAsync(Button("Restore backup…"), Overlay);
                await GiveTheRestorePasswordAndChooseAsync(uploadPath);
                SqliteConnection.ClearAllPools();
                await Button("Confirm restore").ClickAsync();
                await GiveTheBackupPasswordAsync(WebApp.Password);

                await Assertions.Expect(Dialog.GetByText(
                        $"Its size limit, {1_000_000:N0} MB, is more than this disk can safely hold, so it is "))
                    .ToBeVisibleAsync();
                await Button(Dialog.Locator(".modal-footer"), "Close").ClickAsync();
                await Assertions.Expect(Overlay).ToBeHiddenAsync();

                await MaximumSize.FillAsync(original);
                await SaveMaximumSize.ClickAsync();
                await Assertions.Expect(Page.GetByText("Saved — applied after the next ping round.")).ToBeVisibleAsync();
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
        /// Starts noting every label and every percentage the overlay's progress bar is drawn with.
        /// A step can come and go between two of Playwright's looks, so the page watches itself. It
        /// reads each list off what every change replaced as well as off the bar as it stands,
        /// because renders that land together reach an observer as a single call: going by the
        /// bar alone lost the last percentage about one run in thirty, and a short step's label
        /// under load. Labels and percentages are kept apart because a render sets them one at a
        /// time, so a pair read between the two would be a state nobody was shown.
        /// </summary>
        private Task WatchProgressAsync() => Page.EvaluateAsync("""
            () => {
                const seen = window.progressSeen = { labels: [], percents: [] };
                const note = (list, value) => {
                    if (value !== null && value !== list.at(-1)) {
                        list.push(value);
                    }
                };
                const noteBar = bar => {
                    note(seen.labels, bar.getAttribute('aria-label'));
                    note(seen.percents, bar.getAttribute('aria-valuenow'));
                };
                new MutationObserver(records => {
                    let changed = null;
                    for (const { type, target, attributeName, oldValue } of records) {
                        if (type === 'attributes' && target.matches('.progress')) {
                            note(attributeName === 'aria-label' ? seen.labels : seen.percents, oldValue);
                            changed = target;
                        }
                    }

                    // Taken out of the page by now, if the overlay moved on, but still as it was.
                    if (changed) {
                        noteBar(changed);
                    }

                    const bar = document.querySelector('.modal .progress');
                    if (bar) {
                        noteBar(bar);
                    }
                }).observe(document.body, {
                    subtree: true,
                    childList: true,
                    attributeFilter: ['aria-label', 'aria-valuenow'],
                    attributeOldValue: true,
                });
            }
            """);

        /// <summary>
        /// Starts noting whether the overlay, open now, ever closes or says a restore was turned
        /// away, which can come and go between two of Playwright's looks.
        /// </summary>
        private Task WatchTheOverlayAsync() => Page.EvaluateAsync("""
            () => {
                window.overlaySeen = { closed: false, turnedAway: false };
                new MutationObserver(() => {
                    const overlay = document.querySelector('.modal');
                    window.overlaySeen.closed ||= !overlay;
                    window.overlaySeen.turnedAway ||= overlay?.textContent.includes('A restore is under way') ?? false;
                }).observe(document.body, { subtree: true, childList: true, characterData: true });
            }
            """);

        /// <summary>
        /// Presses the overlay's <paramref name="buttons"/> one after the other with no time for
        /// the application to answer in between, which Playwright's clicks would wait for.
        /// </summary>
        private Task PressInOneGoAsync(params string[] buttons) => Page.EvaluateAsync(
            """
            buttons => {
                const footer = [...document.querySelectorAll('.modal-footer button')];
                for (const name of buttons) {
                    footer.find(button => button.textContent.trim() === name).click();
                }
            }
            """,
            buttons);

        /// <summary>What <see cref="WatchProgressAsync"/> has seen so far, each list in order, repeats left out.</summary>
        private async Task<(string[] Labels, int[] Percents)> ProgressSeenAsync()
        {
            var labels = await Page.EvaluateAsync<string[]>("() => window.progressSeen.labels");
            var percents = await Page.EvaluateAsync<string[]>("() => window.progressSeen.percents");
            return (labels, percents.Select(int.Parse).ToArray());
        }

        /// <summary>
        /// That a restore's bar went through its steps in order: the upload by percentage to 100,
        /// then the check, the password and the swap without one. Returns the upload's percentages.
        /// </summary>
        private async Task<int[]> AssertRestoreProgressAsync()
        {
            var (labels, uploaded) = await ProgressSeenAsync();
            Assert.Multiple(() =>
            {
                Assert.That(labels, Is.EqualTo(new[]
                {
                    "Uploading the backup…", "Checking the backup…", "Checking the password…", "Putting the backup in place…",
                }));
                Assert.That(uploaded, Is.Ordered.And.Not.Empty);
                Assert.That(uploaded[^1], Is.EqualTo(100));
            });

            return uploaded;
        }

        /// <summary>The password box in the overlay that confirms a download or a restore.</summary>
        private ILocator CurrentPassword => Page.Locator("#backup-current-password");

        /// <summary>The box for the backup's own password, in the step after the upload and its check.</summary>
        private ILocator BackupPassword => Page.Locator("#backup-password");

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
        /// Waits for the step that asks for the backup's password, which comes once the upload has
        /// arrived and passed its checks, and sends <paramref name="password"/> from it.
        /// </summary>
        private async Task GiveTheBackupPasswordAsync(string password)
        {
            await BackupPassword.FillAsync(password);
            await Button(Dialog, "Restore").ClickAsync();
        }

        /// <summary>Changes the password on the Password page, from <paramref name="tab"/>.</summary>
        private static async Task ChangePasswordAsync(IPage tab, string current, string changed)
        {
            await tab.GotoAsync($"{BaseUrl}/password");
            await tab.Locator("#current-password").FillAsync(current);
            await tab.Locator("#new-password").FillAsync(changed);
            await tab.Locator("#confirm-password").FillAsync(changed);
            await tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Change password", Exact = true }).ClickAsync();
            await Assertions.Expect(tab).Not.ToHaveURLAsync($"{BaseUrl}/password");
        }

        /// <summary>
        /// A faithful copy of the running application's database, taken the way its own backup takes
        /// one, for a test that restores it, or might, to change nothing the other fixtures depend on.
        /// </summary>
        private static async Task<string> FaithfulCopyAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"hostpinger-ui-upload-{Guid.NewGuid():N}.db");
            await using var connection = new SqliteConnection($"Data Source={UiTestRun.DatabasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync();
            return path;
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
