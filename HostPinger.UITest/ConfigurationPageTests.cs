using Microsoft.Data.Sqlite;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// The Configuration page against a real instance: the figures it reads about the database,
    /// and backup and restore, driven the way a person drives them. What the figures are is settled
    /// in DatabaseStatsTests and DatabaseReadsTests, and what <c>DatabaseBackup</c> does to files
    /// in DatabaseBackupTests; what only a browser can settle is that the page shows a figure it
    /// actually read, and the transport around backup and restore — that the download button hands
    /// over a database, and that the file
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

        [Test]
        public async Task Backup_DownloadsAnSqliteDatabase()
        {
            await UnlockedConfigurationPageAsync();

            // The click and the download are the same retry ClickUntilAsync makes elsewhere: a
            // click can land in the beat between the circuit's socket and the components being
            // attached, and that click does nothing at all.
            IDownload? download = null;
            for (var attempt = 0; download is null; attempt++)
            {
                try
                {
                    download = await Page.RunAndWaitForDownloadAsync(
                        () => Button("Download backup").ClickAsync(),
                        new PageRunAndWaitForDownloadOptions { Timeout = 2_000 });
                }
                catch (TimeoutException) when (attempt < 9)
                {
                }
            }

            Assert.That(download.SuggestedFilename, Does.StartWith("hostpinger-backup-").And.EndWith(".db"));

            var header = new byte[16];
            await using (var stream = File.OpenRead((await download.PathAsync())!))
            {
                stream.ReadExactly(header);
            }

            Assert.That(header, Is.EqualTo("SQLite format 3\0"u8.ToArray()),
                "what the browser saves must open as an SQLite database");
        }

        [Test]
        public async Task Restore_CarriesTheChosenFileThroughConfirmationAndRestoresIt()
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

                // The restore button is a label for a hidden file input, so clicking it opens
                // the browser's picker — that part needs no circuit at all. The choice retries
                // for the same reason the click above does: the change event of a file chosen
                // before the components attach is heard by nobody.
                var restore = Button("Restore backup…");
                var confirm = Button("Confirm restore");
                for (var attempt = 0; ; attempt++)
                {
                    var chooser = await Page.RunAndWaitForFileChooserAsync(() => restore.ClickAsync());
                    await chooser.SetFilesAsync(uploadPath);
                    try
                    {
                        await confirm.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = 2_000,
                        });
                        break;
                    }
                    catch (TimeoutException) when (attempt < 9)
                    {
                    }
                }

                // This process holds the file open too: whatever the fixtures seeded through sits in
                // its connection pool, still attached to the file the restore is about to retire,
                // and a seed made afterwards would be written there, where the application never
                // looks. Let go of it first, so every later connection opens the restored file.
                // First rather than after, because a connection to a WAL database that closes last
                // deletes the -wal file by name, and after the swap that name is the new file's.
                SqliteConnection.ClearAllPools();
                await confirm.ClickAsync();

                await Assertions.Expect(Page.GetByText(
                        "Restored — the backup's hosts, history, settings and password now apply."))
                    .ToBeVisibleAsync();
                Assert.That(File.Exists(UiTestRun.DatabasePath + ".pre-restore"),
                    "the database that was replaced must be kept as the undo copy");
            }
            finally
            {
                File.Delete(uploadPath);
            }
        }

        /// <summary>
        /// A backup that passes validation but will not open once swapped in — here a faithful
        /// copy missing one of its tables — is reported beside the control, with the page still
        /// working, and the database it was meant to replace is the one the application runs on
        /// afterwards. Before, the failure escaped the page's catch and ended the circuit, and the
        /// half-restored file stayed in place.
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

                await using (var connection = new SqliteConnection($"Data Source={uploadPath};Pooling=False"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DROP TABLE ResolverErrors;";
                    await command.ExecuteNonQueryAsync();
                }

                await UnlockedConfigurationPageAsync();

                // Chosen and confirmed the same way, and for the same reasons, as the restore above.
                var restore = Button("Restore backup…");
                var confirm = Button("Confirm restore");
                for (var attempt = 0; ; attempt++)
                {
                    var chooser = await Page.RunAndWaitForFileChooserAsync(() => restore.ClickAsync());
                    await chooser.SetFilesAsync(uploadPath);
                    try
                    {
                        await confirm.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = 2_000,
                        });
                        break;
                    }
                    catch (TimeoutException) when (attempt < 9)
                    {
                    }
                }

                SqliteConnection.ClearAllPools();
                await confirm.ClickAsync();

                await Assertions.Expect(Page.GetByText(
                        "The restore failed: The backup has no ResolverErrors table. The previous database is back in place."))
                    .ToBeVisibleAsync();
                await Assertions.Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();

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
