using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Copies the database out as a backup and swaps a validated backup in as a restore. Both
    /// operations hold the <see cref="MaintenanceGate"/>, so they never overlap a ping round or
    /// each other, and both work beside the live file: the hardened service deployments leave the
    /// data directory as the only place this process can write.
    /// </summary>
    public sealed class DatabaseBackup(
        PingerPaths paths,
        IDbContextFactory<HostPingerDbContext> dbFactory,
        UserSettingsStore settings,
        MaintenanceGate maintenanceGate)
    {
        /// <summary>
        /// Room the disk must have beyond the file being written, so a snapshot or an upload
        /// cannot run the data directory to the last byte and take the live database down with it.
        /// </summary>
        public const long FreeSpaceMarginBytes = 64L * 1024 * 1024;

        private const string SnapshotPrefix = "hostpinger-backup-";
        private const string RestorePrefix = "hostpinger-restore-";
        private const string TempExtension = ".tmp";

        private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

        /// <summary>
        /// Where the previous database goes when a restore replaces it: the undo, kept until the
        /// next restore overwrites it. Deliberately not the live file name plus a numbered scheme —
        /// one undo that is always current beats a directory that fills with stale ones.
        /// </summary>
        public string PreRestorePath => paths.DatabasePath + ".pre-restore";

        /// <summary>A fresh path beside the database for an upload to stream into.</summary>
        public string CreateRestoreTempPath() => TempPath(RestorePrefix);

        /// <summary>
        /// Writes a consistent, compacted copy of the database beside it and returns the path.
        /// The caller owns the file — streaming it out and deleting it are its business.
        /// </summary>
        /// <remarks>
        /// <c>VACUUM INTO</c> rather than the online backup API because it leaves the free pages
        /// behind: a database pruned against its size limit is largely empty space, and the copy
        /// should weigh what the data weighs. The gate keeps the monitor from writing meanwhile,
        /// so the copy is of a settled file.
        /// </remarks>
        public async Task<string> CreateSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var snapshotPath = TempPath(SnapshotPrefix);
            using var maintenanceHold = await maintenanceGate.AcquireAsync(cancellationToken);
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                await db.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    var connection = db.Database.GetDbConnection();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM INTO $path;";
                    var target = command.CreateParameter();
                    target.ParameterName = "$path";
                    target.Value = snapshotPath;
                    command.Parameters.Add(target);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }
            catch
            {
                // A failed VACUUM INTO can leave a partial target behind; a half-written backup
                // must not sit there waiting to be downloaded by the retry.
                File.Delete(snapshotPath);
                throw;
            }

            return snapshotPath;
        }

        /// <summary>
        /// Whether <paramref name="candidatePath"/> is a database a restore can accept: null when
        /// it is, and otherwise the sentence to show whoever uploaded it. An older schema passes —
        /// the restore migrates it up, the same as starting a new build against an old file — but
        /// one carrying migrations this build has never heard of is refused rather than guessed at.
        /// </summary>
        public async Task<string?> ValidateBackupAsync(string candidatePath, CancellationToken cancellationToken = default)
        {
            if (!await HasSqliteHeaderAsync(candidatePath, cancellationToken))
            {
                return "This file is not an SQLite database.";
            }

            List<string> applied;
            try
            {
                // Pooling off so no handle outlives this method: a pooled read-only connection
                // would keep the candidate file open and fail the rename that swaps it in.
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = candidatePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString();

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                if (await ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken) is not "ok")
                {
                    return "The file failed SQLite's integrity check.";
                }

                var historyTables = await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';",
                    cancellationToken);
                if (Convert.ToInt64(historyTables) == 0)
                {
                    return "No HostPinger schema was found in this file.";
                }

                applied = [];
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied.Add(reader.GetString(0));
                }
            }
            catch (SqliteException)
            {
                // A file corrupt enough that reading it throws is the same answer as one whose
                // integrity check fails, told less politely.
                return "The file failed SQLite's integrity check.";
            }

            // The migrations this build ships, read from the assembly: the context never opens.
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            return applied.TrueForAll(known.Contains)
                ? null
                : "This backup was made by a newer version of HostPinger.";
        }

        /// <summary>
        /// Replaces the database with the validated file at <paramref name="candidatePath"/>,
        /// keeping what it replaced at <see cref="PreRestorePath"/>, then migrates the new file
        /// and reloads the settings snapshot — so when this returns, the backup's settings and
        /// password are the ones in force. The candidate file is consumed by the swap.
        /// </summary>
        public async Task RestoreAsync(string candidatePath, CancellationToken cancellationToken = default)
        {
            using var maintenanceHold = await maintenanceGate.AcquireAsync(cancellationToken);

            // Pooled idle connections hold the old file open past their last use; on Windows that
            // is enough to fail the swap.
            SqliteConnection.ClearAllPools();

            // A journal left beside the database belongs to the file being retired. SQLite would
            // roll it into whatever sits at the database path, and rolled into the restored file
            // it is corruption, not recovery.
            File.Delete(paths.DatabasePath + "-journal");
            File.Delete(paths.DatabasePath + "-wal");
            File.Delete(paths.DatabasePath + "-shm");

            await SwapIntoPlaceAsync(candidatePath, cancellationToken);

            // The restored file is somebody's old database: migrate it up and re-establish
            // incremental vacuum, exactly as a service start would.
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                await HostPingerDatabase.InitializeAsync(db, cancellationToken);
            }

            // The settings snapshot still describes the file that was just retired. Reloading it
            // puts the backup's settings and password in force — which also invalidates unlock
            // cookies stamped under the old password, with nothing further to do.
            await settings.LoadAsync(cancellationToken);
        }

        /// <summary>What the data directory's disk has free, for the headroom checks.</summary>
        public long AvailableFreeBytes() =>
            new DriveInfo(Path.GetDirectoryName(paths.DatabasePath)!).AvailableFreeSpace;

        /// <summary>
        /// Sweeps snapshot and upload temp files an earlier run left behind — a download that
        /// died with the process, an upload interrupted by a restart. Anything current is held
        /// open, so at startup every one of them is an orphan.
        /// </summary>
        public static void DeleteLeftoverTempFiles(PingerPaths paths)
        {
            var directory = Path.GetDirectoryName(paths.DatabasePath)!;
            foreach (var pattern in new[] { SnapshotPrefix + "*" + TempExtension, RestorePrefix + "*" + TempExtension })
            {
                foreach (var leftover in Directory.EnumerateFiles(directory, pattern))
                {
                    try
                    {
                        File.Delete(leftover);
                    }
                    catch (IOException)
                    {
                        // A file that will not delete is disk clutter, not a reason the service
                        // cannot start.
                    }
                }
            }
        }

        /// <summary>
        /// One atomic replace, so a crash at any moment leaves a complete database on disk under
        /// one name or another — never none. Retried briefly because a page's periodic read can
        /// hold the file at just the wrong instant on Windows.
        /// </summary>
        private async Task SwapIntoPlaceAsync(string candidatePath, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(paths.DatabasePath))
                    {
                        File.Replace(candidatePath, paths.DatabasePath, PreRestorePath, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        // No database to retire — restoring into a fresh install.
                        File.Move(candidatePath, paths.DatabasePath);
                    }

                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    SqliteConnection.ClearAllPools();
                    await Task.Delay(200, cancellationToken);
                }
            }
        }

        private static async Task<bool> HasSqliteHeaderAsync(string candidatePath, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read, SqliteHeader.Length,
                FileOptions.Asynchronous);
            var header = new byte[SqliteHeader.Length];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            return read == header.Length && header.AsSpan().SequenceEqual(SqliteHeader);
        }

        private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        private string TempPath(string prefix) => Path.Combine(
            Path.GetDirectoryName(paths.DatabasePath)!,
            $"{prefix}{Guid.NewGuid():N}{TempExtension}");
    }
}
