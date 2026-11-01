using System.Diagnostics.CodeAnalysis;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Copies the database out as a backup and swaps a validated backup in as a restore. One of
    /// the two runs at a time, and the other is turned away while it does. A restore's swap holds
    /// the <see cref="MaintenanceGate"/>, so it never overlaps a ping round, and shuts the
    /// <see cref="DatabaseFileGate"/>, which keeps every other connection off the file meanwhile;
    /// a snapshot lets the rounds go on. Both work beside the live file: the hardened service
    /// deployments leave the data directory as the only place this process can write.
    /// </summary>
    public sealed class DatabaseBackup(
        PingerPaths paths,
        IDbContextFactory<HostPingerDbContext> dbFactory,
        UserSettingsStore settings,
        MaintenanceGate maintenanceGate,
        DatabaseFileGate fileGate,
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

        /// <summary>A temp file itself, and the names SQLite gives what it keeps beside one.</summary>
        private static readonly string[] TempFileSuffixes = [string.Empty, "-journal", "-wal", "-shm"];

        private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

        private const string DefinesCodeRefusal = "This file defines triggers or views, which a HostPinger database never has.";

        private const string DefinesCodeQuery = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('trigger', 'view');";

        private const string DataBytesQuery =
            "SELECT (page_count - freelist_count) * page_size FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size();";

        private const string StayedInUse = "The database stayed in use, so nothing was replaced. Try the restore again.";

        private const string SnapshotUnderWay = "A backup is being written";

        private const string RestoreUnderWay = "A restore is under way";

        private readonly object _turnLock = new();

        // Which of the two holds the turn they share, to tell whichever asks next; null while
        // neither does. See TryTakeTurn.
        private string? _underWay;

        /// <summary>
        /// How long a restore waits for the file it is retiring to be let go of before it gives up,
        /// first by the application's own connections and then by SQLite: the provider's own
        /// default. Settable for the tests, which would otherwise spend half a minute holding a
        /// file open to see the restore refuse.
        /// </summary>
        internal int BusyTimeoutSeconds { get; init; } = 30;

        /// <summary>
        /// How often a snapshot under way says how far it has got. Settable for the tests, whose
        /// databases are copied in less time than this.
        /// </summary>
        internal TimeSpan SnapshotProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// What the disk beside the database has free. Settable for the tests, which cannot fill a
        /// disk to see a restore refuse for want of room.
        /// </summary>
        internal Func<long> MeasureFreeBytes { get; init; } =
            () => new DriveInfo(Path.GetDirectoryName(paths.DatabasePath)!).AvailableFreeSpace;

        /// <summary>
        /// Called once the backup is in place, before it is opened. For the tests, which have no
        /// other way to act at that moment: to leave the page there, or to have opening it fail.
        /// </summary>
        internal Func<Task>? AfterSwap { get; init; }

        /// <summary>
        /// Where the previous database goes when a restore replaces it: the undo, kept until the
        /// next restore overwrites it. Deliberately not the live file name plus a numbered scheme —
        /// one undo that is always current beats a directory that fills with stale ones.
        /// </summary>
        public string PreRestorePath => paths.DatabasePath + ".pre-restore";

        /// <summary>A fresh path beside the database for an upload to be received into.</summary>
        internal string CreateRestoreTempPath() => TempPath(RestorePrefix);

        /// <summary>
        /// Takes the one turn a snapshot and a restore share, or says which of them holds it. One at
        /// a time, for two reasons. Each writes a file the size of the database beside it, having
        /// checked the disk has room for one, and two at once would each count on the same room.
        /// And a restore retires the database it replaces as the undo copy, so a second restore
        /// finishing after the first would retire the first one's file in place of the database
        /// the person meant to keep.
        /// </summary>
        private bool TryTakeTurn(string what, [NotNullWhen(false)] out string? refusal)
        {
            lock (_turnLock)
            {
                if (_underWay is { } other)
                {
                    refusal = $"{other}. Try again once it has finished.";
                    return false;
                }

                _underWay = what;
            }

            refusal = null;
            return true;
        }

        private void ReleaseTurn()
        {
            lock (_turnLock)
            {
                _underWay = null;
            }
        }

        /// <summary>
        /// Null when the disk beside the database has <paramref name="bytes"/> free beyond the
        /// margin, and otherwise the sentence that says <paramref name="what"/> needs more.
        /// </summary>
        private string? RoomRefusal(long bytes, string what)
        {
            var needed = bytes + FreeSpaceMarginBytes;
            var free = MeasureFreeBytes();
            return free < needed
                ? $"{what} needs about {needed >> 20} MB free beside the database. The disk has {free >> 20} MB."
                : null;
        }

        /// <summary>
        /// Writes a consistent, compacted copy of the database beside it and returns the path.
        /// The caller owns the file — streaming it out and deleting it are its business. If
        /// <paramref name="progress"/> is given, it hears how far the copy has got as a fraction, a
        /// few times a second, and 1 once the copy is done. Cancelling stops the copy where it is,
        /// and what had been written is deleted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>VACUUM INTO</c> rather than the online backup API because it leaves the free pages
        /// behind: a database pruned against its size limit is largely empty space, and the copy
        /// should weigh what the data weighs.
        /// </para>
        /// <para>
        /// It reads the file in one transaction, so in WAL mode the copy is of the moment it began,
        /// whatever the ping rounds write meanwhile, and they go on writing. They used to wait at the
        /// <see cref="MaintenanceGate"/> for the whole copy, which left a gap that long in every
        /// host's history: 75 seconds for the benchmarks' production-shaped database on the
        /// development machine. In rollback-journal mode that read would keep a round from
        /// committing until the provider gave up and the round's pings were lost, so there the
        /// rounds still wait for the copy at the gate.
        /// </para>
        /// <para>
        /// SQLite says nothing about how far a <c>VACUUM INTO</c> has got, so the fraction is the
        /// copy's size on disk against the database's pages that hold anything, which the copy
        /// writes out in turn. On a copy of the production database it grew steadily to within a
        /// percent of that. A file with rows deleted here and there packs tighter in the copy,
        /// which then ends smaller, so the fraction stops short until the 1 at the end.
        /// </para>
        /// <para>
        /// Runs on the thread pool, for the reason <see cref="ValidateBackupAsync"/> gives: a page
        /// waits on it, and a gigabyte takes seconds. Microsoft.Data.Sqlite looks at the token only
        /// before a statement starts, so a cancel interrupts the connection instead.
        /// </para>
        /// <para>
        /// Every failure is an <see cref="IOException"/>, as a restore's are, and a cancel is an
        /// <see cref="OperationCanceledException"/>. So is a refusal before anything is written: a
        /// snapshot or a restore already under way, or too little room beside the database for the
        /// copy, which is checked here, in turn, rather than by the page before it.
        /// </para>
        /// </remarks>
        public Task<string> CreateSnapshotAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.Run(() => CreateSnapshotOnThisThreadAsync(progress, cancellationToken), cancellationToken);

        private async Task<string> CreateSnapshotOnThisThreadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (!TryTakeTurn(SnapshotUnderWay, out var refusal))
            {
                throw new IOException(refusal);
            }

            try
            {
                return await WriteSnapshotAsync(progress, cancellationToken);
            }
            finally
            {
                ReleaseTurn();
            }
        }

        private async Task<string> WriteSnapshotAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var snapshotPath = TempPath(SnapshotPrefix);

            // Asked before the copy's own connection opens, so the gate is always taken before a
            // pass at the file gate, as a round takes them. Only a restore changes the mode while
            // the service runs, and none can start while this holds the turn.
            using var maintenanceHold = await IsInWalModeAsync(cancellationToken)
                ? null
                : await maintenanceGate.AcquireAsync(cancellationToken);
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                await db.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    var connection = (SqliteConnection)db.Database.GetDbConnection();
                    var dataBytes = Convert.ToInt64(await ScalarAsync(connection, DataBytesQuery, cancellationToken));

                    // The copy weighs about what the data does. Checked in turn, so nothing else of
                    // this class is writing beside the database meanwhile; a snapshot still waiting
                    // to be downloaded is on the disk already, and counted.
                    if (RoomRefusal(dataBytes, "Writing the snapshot") is { } noRoom)
                    {
                        throw new IOException(noRoom);
                    }

                    await using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM INTO $path;";
                    command.Parameters.AddWithValue("$path", snapshotPath);

                    using var stopWatching = new CancellationTokenSource();
                    var watching = progress is null
                        ? Task.CompletedTask
                        : WatchSnapshotAsync(snapshotPath, dataBytes, progress, stopWatching.Token);
                    try
                    {
                        // Let go of before the connection goes back to the pool, so a cancel that
                        // comes late cannot interrupt whatever uses the connection next.
                        using (cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle)))
                        {
                            await command.ExecuteNonQueryAsync(cancellationToken);
                        }
                    }
                    finally
                    {
                        await stopWatching.CancelAsync();
                        await watching;
                    }
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }

                // A cancel that came once the copy was done, too late to interrupt it.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex)
            {
                // A failed or interrupted VACUUM INTO can leave a partial target behind; a
                // half-written backup must not sit there waiting to be downloaded by the retry.
                File.Delete(snapshotPath);
                if (ex is not SqliteException)
                {
                    throw;
                }

                // An interrupted copy is the cancel it was interrupted for. Anything else SQLite
                // stopped on, such as a disk that filled, goes to the page as an IOException, which
                // it can catch without knowing SQLite's exception.
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The snapshot was cancelled.", ex, cancellationToken);
                }

                throw new IOException(ex.Message, ex);
            }

            progress?.Report(1);
            return snapshotPath;
        }

        /// <summary>Whether the database is in WAL mode, where a read never holds up a ping round's write.</summary>
        private async Task<bool> IsInWalModeAsync(CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            return await ScalarAsync(connection, "PRAGMA journal_mode;", cancellationToken) is "wal";
        }

        /// <summary>
        /// Tells <paramref name="progress"/> how much of <paramref name="dataBytes"/> the snapshot
        /// has reached, every <see cref="SnapshotProgressInterval"/> until stopped. Never 1, even
        /// when the copy outgrows the estimate: 1 is said once, when the copy is done.
        /// </summary>
        private async Task WatchSnapshotAsync(string snapshotPath, long dataBytes, IProgress<double> progress, CancellationToken stop)
        {
            try
            {
                using var timer = new PeriodicTimer(SnapshotProgressInterval);
                while (await timer.WaitForNextTickAsync(stop))
                {
                    progress.Report(Math.Min((double)WrittenBytes(snapshotPath) / Math.Max(dataBytes, 1), 0.99));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// How much of the file has been written so far, asked through a handle of its own rather
        /// than FileInfo: Windows can keep an older length in the directory entry while another
        /// handle is still writing the file. Nothing yet when SQLite has not created it.
        /// </summary>
        private static long WrittenBytes(string path)
        {
            try
            {
                using var file = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                return file.Length;
            }
            catch (FileNotFoundException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Whether <paramref name="candidatePath"/> is a database a restore can accept: null when
        /// it is, and otherwise the sentence to show whoever uploaded it. An older schema passes —
        /// the restore migrates it up, the same as starting a new build against an old file — but
        /// one carrying migrations this build has never heard of is refused rather than guessed at,
        /// and so is one defining anything that would run inside this application.
        /// </summary>
        /// <remarks>
        /// Runs on the thread pool: Microsoft.Data.Sqlite does no asynchronous I/O, and the integrity
        /// check reads the whole file, which for a gigabyte is seconds the page asking would
        /// otherwise sit frozen through.
        /// </remarks>
        internal Task<string?> ValidateBackupAsync(string candidatePath, CancellationToken cancellationToken = default) =>
            Task.Run(() => ValidateOnThisThreadAsync(candidatePath, cancellationToken), cancellationToken);

        private async Task<string?> ValidateOnThisThreadAsync(string candidatePath, CancellationToken cancellationToken)
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
                if (Convert.ToInt64(await ScalarAsync(connection, DefinesCodeQuery, cancellationToken)) > 0)
                {
                    return DefinesCodeRefusal;
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
            var known = db.Database.GetMigrations().ToList();
            if (!applied.TrueForAll(known.Contains))
            {
                return "This backup was made by a newer version of HostPinger.";
            }

            // An older backup is migrated beside the database before it goes into place, and the
            // migrations since 1.12.0 rebuild the history's indexes: on a production-sized file the
            // journal for that reached about a third of the file. Refused here rather than left to
            // fill the disk partway through, with half the file's size as the room it needs.
            if (known.Except(applied).Any()
                && RoomRefusal(new FileInfo(candidatePath).Length / 2, "This backup is from an older version, and migrating it") is { } noRoom)
            {
                return noRoom;
            }

            return null;
        }

        /// <summary>
        /// Replaces the database with the validated file at <paramref name="candidatePath"/>,
        /// keeping what it replaced at <see cref="PreRestorePath"/>, and reloads the settings
        /// snapshot — so when this returns, the backup's settings and password are the ones in
        /// force. The candidate file is consumed by the swap. The restored database is in WAL
        /// mode, and the one kept at <see cref="PreRestorePath"/> is in rollback-journal mode,
        /// whole without a -wal beside it.
        /// <para>
        /// The candidate is migrated and checked where it lies, before anything is replaced and
        /// before the maintenance gate is taken: migrating an old backup of a production-sized
        /// database takes minutes, and ping rounds go on meanwhile rather than leaving a gap in
        /// the history. Only the swap itself holds the gate.
        /// </para>
        /// <para>
        /// Every failure is an <see cref="IOException"/>, and none leaves the service without a
        /// database it can start on. A backup that cannot be migrated or lacks part of the schema
        /// is refused with nothing replaced. After the swap, a restored file that will not open or
        /// give up its settings is taken out again and the database it replaced put back, in
        /// rollback-journal mode; on a fresh install, where there was nothing to put back, an empty
        /// database takes its place.
        /// </para>
        /// <para>
        /// Runs on the thread pool, for the reason <see cref="ValidateBackupAsync"/> gives: an old
        /// backup's migration would otherwise hold the page asking for minutes.
        /// </para>
        /// </summary>
        internal Task RestoreAsync(string candidatePath, CancellationToken cancellationToken = default) =>
            Task.Run(
                async () =>
                {
                    if (!TryTakeTurn(RestoreUnderWay, out var refusal))
                    {
                        throw new IOException(refusal);
                    }

                    try
                    {
                        await RestoreOnThisThreadAsync(candidatePath, cancellationToken);
                    }
                    finally
                    {
                        ReleaseTurn();
                    }
                },
                CancellationToken.None);

        /// <summary>
        /// Restores the backup <paramref name="source"/> delivers, <paramref name="length"/> bytes of
        /// it: receives it beside the database, checks it as <see cref="ValidateBackupAsync"/> does,
        /// and puts it in place as <see cref="RestoreAsync"/> does, all in the one turn, so that
        /// nothing else of this class writes beside the database from the room check to the swap.
        /// <paramref name="progress"/> hears which of the three is under way, and how much of the
        /// upload has arrived while it arrives.
        /// </summary>
        /// <returns>
        /// Null once the backup is in force. Otherwise the sentence to show whoever sent it, which
        /// means nothing was replaced: a snapshot or another restore under way, too little room
        /// beside the database for the file, or a refusal of the file itself.
        /// </returns>
        /// <remarks>
        /// Every failure is an <see cref="IOException"/>, as <see cref="RestoreAsync"/>'s are,
        /// except whatever reading <paramref name="source"/> throws, which comes out as it is: for a
        /// page, the browser failing to read the file it chose. However it ends, the file received
        /// does not outlive this call.
        /// </remarks>
        public async Task<string?> RestoreUploadAsync(
            Stream source,
            long length,
            IProgress<RestoreProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!TryTakeTurn(RestoreUnderWay, out var refusal))
            {
                return refusal;
            }

            try
            {
                if (RoomRefusal(length, "Receiving the backup") is { } noRoom)
                {
                    return noRoom;
                }

                var candidatePath = CreateRestoreTempPath();
                try
                {
                    progress?.Report(new RestoreProgress(RestoreStep.Receiving, 0));
                    await ReceiveAsync(source, candidatePath, length, progress, cancellationToken);

                    progress?.Report(new RestoreProgress(RestoreStep.Checking, null));
                    if (await ValidateBackupAsync(candidatePath, cancellationToken) is { } invalid)
                    {
                        return invalid;
                    }

                    progress?.Report(new RestoreProgress(RestoreStep.Replacing, null));
                    await Task.Run(() => RestoreOnThisThreadAsync(candidatePath, cancellationToken), CancellationToken.None);
                    return null;
                }
                finally
                {
                    // The swap consumes the file; this catches every earlier exit — an upload that
                    // failed, a file refused, a restore that replaced nothing.
                    File.Delete(candidatePath);
                }
            }
            finally
            {
                ReleaseTurn();
            }
        }

        /// <summary>
        /// Writes what <paramref name="source"/> delivers to <paramref name="path"/>, telling
        /// <paramref name="progress"/> how much of <paramref name="length"/> has arrived, and
        /// refuses anything past <paramref name="length"/>, which is all the room was checked for.
        /// </summary>
        /// <remarks>
        /// Asynchronous all the way down rather than on the thread pool: the file is opened for
        /// asynchronous writes, and a page's upload arrives over its circuit, so a page that awaits
        /// this is free between the pieces.
        /// </remarks>
        private static async Task ReceiveAsync(
            Stream source,
            string path,
            long length,
            IProgress<RestoreProgress>? progress,
            CancellationToken cancellationToken)
        {
            await using var target = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
            var buffer = new byte[81_920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += read;
                if (received > length)
                {
                    throw new IOException($"More arrived than the {length:N0} bytes the file was said to hold.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(new RestoreProgress(RestoreStep.Receiving, length > 0 ? (double)received / length : 1));
            }
        }

        private async Task RestoreOnThisThreadAsync(string candidatePath, CancellationToken cancellationToken)
        {
            await PrepareCandidateAsync(candidatePath, cancellationToken);

            using var maintenanceHold = await maintenanceGate.AcquireAsync(cancellationToken);

            // The host summary keeps a connection of its own open, outside the pool that
            // ClearAllPools empties, so it lets go of the file first and is resumed on whatever sits
            // at the path afterwards — the restored file, or the old one if the swap never happened.
            await hostSummaries.SuspendAsync(cancellationToken);
            try
            {
                // The pages' reads and writes take no turns at the gate above. The file gate waits
                // for the connections they have open, and keeps new ones back through everything
                // that follows: emptying the pool, the swap, opening the restored file and loading
                // its settings, or putting the old file back.
                await fileGate.WhileShutAsync(
                    TimeSpan.FromSeconds(BusyTimeoutSeconds),
                    () => ReplaceAsync(candidatePath, cancellationToken),
                    cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new IOException(StayedInUse, ex);
            }
            finally
            {
                hostSummaries.Resume();
            }
        }

        /// <summary>
        /// Brings the candidate up to date and checks it has every table and column this build
        /// reads, then puts it in WAL mode, all on connections of its own that are closed again
        /// before the swap. Validation reads the migration history, not the tables it vouches
        /// for, and a file can carry the one without the others; checked after the migrations,
        /// because an older backup is only complete after them.
        /// </summary>
        private async Task PrepareCandidateAsync(string candidatePath, CancellationToken cancellationToken)
        {
            try
            {
                // Out of the pool, so that nothing holds the candidate open across the swap.
                var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                    .UseSqlite(new SqliteConnectionStringBuilder { DataSource = candidatePath, Pooling = false }.ToString())
                    .Options;
                await using (var db = new HostPingerDbContext(options))
                {
                    // Validation refuses these already. Asked again because migrating the file writes
                    // to it, which would run a trigger, and because a restore that relied on its
                    // caller having validated would let the next caller that forgot plant one.
                    await db.Database.OpenConnectionAsync(cancellationToken);
                    try
                    {
                        await using var command = db.Database.GetDbConnection().CreateCommand();
                        command.CommandText = DefinesCodeQuery;
                        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0)
                        {
                            throw new InvalidDataException(DefinesCodeRefusal);
                        }
                    }
                    finally
                    {
                        await db.Database.CloseConnectionAsync();
                    }

                    await HostPingerDatabase.InitializeAsync(db, cancellationToken);
                    if (await FindMissingSchemaAsync(db, cancellationToken) is { } missing)
                    {
                        throw new InvalidDataException($"The backup has no {missing}.");
                    }
                }

                await EnterWalAsync(candidatePath, BusyTimeoutSeconds, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A migration that fails on the file, a disk that fills partway through it: the
                // live database has not been touched, which is what the reader needs to know.
                throw new IOException($"{ex.Message} Nothing was replaced.", ex);
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

                retired = await SwapIntoPlaceAsync(candidatePath, cancellationToken);
            }
            catch (SqliteException ex)
            {
                // A file SQLite will not work on is a failed restore to report, like the file kept
                // busy above.
                throw new IOException($"{ex.Message} Nothing was replaced.", ex);
            }

            // From here the restore finishes or is undone, whatever becomes of the page that asked
            // for it: given up halfway, it would leave the backup's file in place under the
            // settings and password of the one it replaced.
            try
            {
                if (AfterSwap is { } afterSwap)
                {
                    await afterSwap();
                }

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
        /// Opens the file just swapped in and loads its settings. The candidate was migrated and
        /// checked before the swap, so the migrations and the check here normally find nothing to
        /// do; they are run again because they are what a service start would run on the file,
        /// and cheap once there is nothing to apply.
        /// </summary>
        private async Task OpenRestoredAsync(CancellationToken cancellationToken)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                await HostPingerDatabase.InitializeAsync(db, cancellationToken);
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
        /// in progress on it, for as long as the provider retries a busy command. The application's
        /// own connections have been waited for at the <see cref="DatabaseFileGate"/> by now, so
        /// what is left to wait for here is a connection from outside it, another process's. That
        /// wait is what keeps the restored file safe: the last connection to close on a WAL
        /// database deletes the -wal and -shm beside it by name, and after the swap those names are
        /// the restored file's. A file already in rollback-journal mode has neither, and is left as
        /// it is.
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
                throw new IOException(StayedInUse, ex);
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
        internal long AvailableFreeBytes() => MeasureFreeBytes();

        /// <summary>
        /// Sweeps snapshot and upload temp files an earlier run left behind — a download that
        /// died with the process, an upload interrupted by a restart — and what SQLite keeps beside
        /// them while it writes one. VACUUM INTO journals the copy it writes, and a restore migrates
        /// an upload in rollback-journal mode and then puts it in WAL mode, so a run stopped partway
        /// through leaves a journal, a -wal or a -shm under the temp file's name; the migration's
        /// journal can reach a third of the upload. Anything current is held open, so at startup
        /// every one of them is an orphan.
        /// </summary>
        public static void DeleteLeftoverTempFiles(PingerPaths paths)
        {
            var directory = Path.GetDirectoryName(paths.DatabasePath)!;
            foreach (var prefix in new[] { SnapshotPrefix, RestorePrefix })
            {
                foreach (var leftover in Directory.EnumerateFiles(directory, prefix + "*")
                    .Where(path => TempFileSuffixes.Any(suffix => path.EndsWith(TempExtension + suffix, StringComparison.Ordinal))))
                {
                    try
                    {
                        File.Delete(leftover);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that will not delete is disk clutter, not a reason the service
                        // cannot start. Windows refuses a read-only file, or one its permissions
                        // deny, with UnauthorizedAccessException rather than IOException.
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

    /// <summary>Which part of a restore from an upload is under way.</summary>
    public enum RestoreStep
    {
        /// <summary>The upload arriving, and being written beside the database.</summary>
        Receiving,

        /// <summary>The file received being checked, as <see cref="DatabaseBackup.ValidateBackupAsync"/> checks it.</summary>
        Checking,

        /// <summary>The file being migrated if it needs it, and swapped in.</summary>
        Replacing,
    }

    /// <summary>How far a restore from an upload has got.</summary>
    /// <param name="Step">The part under way.</param>
    /// <param name="Fraction">
    /// How much of the upload has arrived, while it is arriving; null for the parts SQLite gives no
    /// measure of.
    /// </param>
    public readonly record struct RestoreProgress(RestoreStep Step, double? Fraction);
}
