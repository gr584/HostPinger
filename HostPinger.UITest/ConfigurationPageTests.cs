using Microsoft.Data.Sqlite;
using Microsoft.Playwright;

namespace HostPinger.UITest
{
    /// <summary>
    /// Backup and restore, driven the way a person drives them. What <c>DatabaseBackup</c> does
    /// to files is settled in DatabaseBackupTests; what only a browser can settle is the
    /// transport around it — that the download button hands over a database, and that the file
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
