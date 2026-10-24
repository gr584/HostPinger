using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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
        MaintenanceGate maintenanceGate,
        HostSummaryCache hostSummaries)
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
        /// How long a restore waits for the file it is retiring to be let go of before it gives up:
        /// the provider's own default. Settable for the tests, which would otherwise spend half a
        /// minute holding a file open to see the restore refuse.
        /// </summary>
        internal int BusyTimeoutSeconds { get; init; } = 30;

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
        /// one carrying migrations this build has never heard of is refused rather than guessed at,
        /// and so is one defining anything that would run inside this application.
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

                // The migrations create no triggers and no views, so a file holding either was
                // shaped by something else, and a trigger runs inside this application whenever
                // its table is written: one on the settings table can write its own password back
                // after every change, outlasting both the Password page and --remove-password.
                var definitions = await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('trigger', 'view');",
                    cancellationToken);
                if (Convert.ToInt64(definitions) > 0)
                {
                    return "This file defines triggers or views, which a HostPinger database never has.";
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
        /// password are the ones in force. The candidate file is consumed by the swap. The restored
        /// database is in WAL mode, and the one kept at <see cref="PreRestorePath"/> is in
        /// rollback-journal mode, whole without a -wal beside it.
        /// <para>
        /// Every failure is an <see cref="IOException"/>, and none leaves the service without a
        /// database it can start on. Before the swap nothing has been replaced. After it, a
        /// restored file that cannot be migrated, lacks part of the schema or will not give up its
        /// settings is taken out again and the database it replaced put back, in rollback-journal
        /// mode; on a fresh install, where there was nothing to put back, an empty database takes
        /// its place.
        /// </para>
        /// </summary>
        public async Task RestoreAsync(string candidatePath, CancellationToken cancellationToken = default)
        {
            using var maintenanceHold = await maintenanceGate.AcquireAsync(cancellationToken);

            // The host summary keeps a connection of its own open, outside the pool that
            // ClearAllPools empties, so it lets go of the file first and is resumed on whatever sits
            // at the path afterwards — the restored file, or the old one if the swap never happened.
            await hostSummaries.SuspendAsync(cancellationToken);
            try
            {
                await ReplaceAsync(candidatePath, cancellationToken);
            }
            finally
            {
                hostSummaries.Resume();
            }
        }

        private async Task ReplaceAsync(string candidatePath, CancellationToken cancellationToken)
        {
            bool retired;
            try
            {
                // Pooled idle connections hold the old file open past their last use; on Windows
                // that is enough to fail the swap.
                SqliteConnection.ClearAllPools();

                // A fresh install has no file there to take out of anything.
                if (File.Exists(paths.DatabasePath))
                {
                    await LeaveWalAsync(paths.DatabasePath, BusyTimeoutSeconds, cancellationToken);
                }

                // A journal left beside the database belongs to the file being retired. SQLite
                // would roll it into whatever sits at the database path, and rolled into the
                // restored file it is corruption, not recovery.
                DeleteSidecars();

                await EnterWalAsync(candidatePath, BusyTimeoutSeconds, cancellationToken);
                retired = await SwapIntoPlaceAsync(candidatePath, cancellationToken);
            }
            catch (SqliteException ex)
            {
                // A file SQLite will not work on — a disk too full to change the candidate's
                // journal mode — is a failed restore to report, like the file kept busy above.
                throw new IOException($"{ex.Message} Nothing was replaced.", ex);
            }

            // From here the restore finishes or is undone, whatever becomes of the page that asked
            // for it: given up halfway, it would leave the backup's file in place under the
            // settings and password of the one it replaced. Migrating a large old backup takes
            // minutes, which is plenty of time to navigate away.
            try
            {
                await OpenRestoredAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                try
                {
                    await PutBackAsync(retired);
                }
                catch (Exception putBackFailure)
                {
                    throw new IOException(
                        $"{ex.Message} Putting the previous database back failed as well: {putBackFailure.Message}"
                            + (retired ? $" It is kept at {PreRestorePath}." : string.Empty),
                        ex);
                }

                throw new IOException(
                    ex.Message + (retired
                        ? " The previous database is back in place."
                        : " An empty database is in its place, as there was none before."),
                    ex);
            }
        }

        /// <summary>
        /// Brings the file just swapped in up to date and loads its settings, refusing it if it
        /// does not have every table and column this build reads.
        /// </summary>
        private async Task OpenRestoredAsync(CancellationToken cancellationToken)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                // The restored file is somebody's old database: migrate it up and re-establish
                // incremental vacuum, exactly as a service start would.
                await HostPingerDatabase.InitializeAsync(db, cancellationToken);

                // Validation reads the migration history, not the tables it vouches for, and a file
                // can carry the one without the others. Checked here, once the migrations have run,
                // rather than up front, because an older backup is only complete after them.
                if (await FindMissingSchemaAsync(db, cancellationToken) is { } missing)
                {
                    throw new InvalidDataException($"The backup has no {missing}.");
                }
            }

            // The settings snapshot still describes the file that was just retired. Reloading it
            // puts the backup's settings and password in force — which also invalidates unlock
            // cookies stamped under the old password, with nothing further to do.
            await settings.LoadAsync(cancellationToken);
        }

        /// <summary>
        /// The first table or column of the model that the open database lacks, named for the
        /// person restoring, or null when it has them all.
        /// </summary>
        private static async Task<string?> FindMissingSchemaAsync(HostPingerDbContext db, CancellationToken cancellationToken)
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                foreach (var entity in db.Model.GetEntityTypes())
                {
                    if (entity.GetTableName() is not { } table)
                    {
                        continue;
                    }

                    // SQLite matches names without regard to case, and so does this.
                    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT name FROM pragma_table_info($table);";
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = "$table";
                        parameter.Value = table;
                        command.Parameters.Add(parameter);
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            columns.Add(reader.GetString(0));
                        }
                    }

                    if (columns.Count == 0)
                    {
                        return $"{table} table";
                    }

                    var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
                    foreach (var property in entity.GetProperties())
                    {
                        if (property.GetColumnName(storeObject) is { } column && !columns.Contains(column))
                        {
                            return $"{column} column in its {table} table";
                        }
                    }
                }

                return null;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        /// <summary>
        /// Takes a restored file that would not open back out, and puts the database it replaced
        /// at the path again — or, when there was none, an empty one, so the service keeps a
        /// database it can start on either way.
        /// </summary>
        private async Task PutBackAsync(bool retired)
        {
            // The same brief retries as the swap: on Windows a connection let go of a moment ago
            // can still hold the file.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    SqliteConnection.ClearAllPools();

                    // The restored file's journal, -wal and -shm are its own; beside the file put
                    // back, SQLite would read them as that file's.
                    DeleteSidecars();
                    if (retired)
                    {
                        File.Move(PreRestorePath, paths.DatabasePath, overwrite: true);
                    }
                    else
                    {
                        File.Delete(paths.DatabasePath);
                    }

                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(200);
                }
            }

            if (!retired)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                await HostPingerDatabase.InitializeAsync(db);
            }

            await settings.LoadAsync();
        }

        private void DeleteSidecars()
        {
            File.Delete(paths.DatabasePath + "-journal");
            File.Delete(paths.DatabasePath + "-wal");
            File.Delete(paths.DatabasePath + "-shm");
        }

        /// <summary>
        /// Takes the file about to be retired out of WAL mode. Leaving it, SQLite folds whatever the
        /// -wal still holds into the file, so the undo copy is whole on its own; and it only leaves
        /// once no other connection has the file open, so this is also what waits out a read still
        /// in progress on it, for as long as the provider retries a busy command. That wait is what
        /// keeps the restored file safe: the last connection to close on a WAL database deletes the
        /// -wal and -shm beside it by name, and after the swap those names are the restored file's.
        /// A file already in rollback-journal mode has neither, and is left as it is.
        /// </summary>
        private static async Task LeaveWalAsync(string databasePath, int busyTimeoutSeconds, CancellationToken cancellationToken)
        {
            try
            {
                await SetJournalModeAsync(databasePath, "DELETE", busyTimeoutSeconds, cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                // SQLITE_BUSY and SQLITE_LOCKED: something held the file for the whole wait. An
                // IOException, so the page reports it as a failed restore like any other.
                throw new IOException(
                    "The database stayed in use, so nothing was replaced. Try the restore again.", ex);
            }
        }

        /// <summary>
        /// Puts the file about to go into place into WAL mode. A backup is written by VACUUM INTO,
        /// which leaves it in rollback-journal mode, and there a read keeps a write from committing: a ping round that comes due during
        /// a long read waits on it, and past the timeout gives up and loses its pings. Done while
        /// the file is still the candidate, so nothing opens the restored database in the old mode.
        /// Where the file system cannot do WAL, SQLite says so by keeping the old mode, and the
        /// restore goes on in it, as a new database does.
        /// </summary>
        private static Task EnterWalAsync(string candidatePath, int busyTimeoutSeconds, CancellationToken cancellationToken) =>
            SetJournalModeAsync(candidatePath, "WAL", busyTimeoutSeconds, cancellationToken);

        /// <summary>
        /// Sets a file's journal mode on a connection of its own, outside the pool so that it is
        /// closed when this returns rather than kept, holding the file, across the swap.
        /// </summary>
        private static async Task SetJournalModeAsync(string path, string mode, int busyTimeoutSeconds, CancellationToken cancellationToken)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = busyTimeoutSeconds,
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await ScalarAsync(connection, $"PRAGMA journal_mode = {mode};", cancellationToken);
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
        /// hold the file at just the wrong instant on Windows. Returns whether a database was
        /// retired to <see cref="PreRestorePath"/>, which is what an undo would put back.
        /// </summary>
        private async Task<bool> SwapIntoPlaceAsync(string candidatePath, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(paths.DatabasePath))
                    {
                        File.Replace(candidatePath, paths.DatabasePath, PreRestorePath, ignoreMetadataErrors: true);
                        return true;
                    }

                    // No database to retire — restoring into a fresh install. Whatever sits at
                    // PreRestorePath is an earlier restore's undo, not this one's.
                    File.Move(candidatePath, paths.DatabasePath);
                    return false;
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
