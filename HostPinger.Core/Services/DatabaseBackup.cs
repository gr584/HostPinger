using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

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
        HostSummaryCache hostSummaries,
        ILogger<DatabaseBackup> logger)
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

        private const string NoSchemaRefusal = "No HostPinger schema was found in this file.";

        private const string DefinesCodeRefusal = "This file defines triggers or views, which a HostPinger database never has.";

        private const string DefinesCodeQuery = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('trigger', 'view');";

        private const string DataBytesQuery =
            "SELECT (page_count - freelist_count) * page_size FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size();";

        private const string StayedInUse = "The database stayed in use, so nothing was replaced. Try the restore again.";

        private const string SnapshotUnderWay = "A backup is being written";

        private const string RestoreUnderWay = "A restore is under way";

        private const string PasswordWrong = "That is not the backup's password.";

        private const string PasswordNotGiven =
            "The backup's password was not given in time, so the upload was let go of and nothing was replaced.";

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
        /// How long a restore waits for an answer when it asks for a backup's password, before it
        /// lets the upload go. It holds the turn and keeps the upload on the disk while it waits, so
        /// an overlay left open must not keep every backup and restore off for good. Settable for
        /// the tests, which would otherwise spend ten minutes watching it run out.
        /// </summary>
        internal TimeSpan PasswordWait { get; init; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Called with the database a restore replaces still at the path, whole and out of WAL mode,
        /// just before the backup goes over it. For the tests, which copy it there to see what
        /// became of a write that raced the restore.
        /// </summary>
        internal Func<Task>? BeforeReplacing { get; init; }

        /// <summary>
        /// Called once the backup is in place, before its settings are put in force. For the tests,
        /// which have no other way to act at that moment: to leave the page there.
        /// </summary>
        internal Func<Task>? AfterSwap { get; init; }

        /// <summary>A fresh path beside the database for an upload to be received into.</summary>
        internal string CreateRestoreTempPath() => TempPath(RestorePrefix);

        /// <summary>
        /// Takes the one turn a snapshot and a restore share, or says which of them holds it. One at
        /// a time, because each writes a file the size of the database beside it, having checked the
        /// disk has room for one, and two at once would each count on the same room.
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
                    return NoSchemaRefusal;
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
                    if (reader.IsDBNull(0))
                    {
                        return NoSchemaRefusal;
                    }

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
        /// Replaces the database with the validated file at <paramref name="candidatePath"/> and
        /// puts the backup's settings in force, so when this returns, its settings and password are
        /// the ones in force. The candidate file is consumed by the swap. The restored database is
        /// in WAL mode.
        /// <para>
        /// The candidate is brought up to date, checked and read where it lies, before anything is
        /// replaced and before the maintenance gate is taken: migrating an old backup of a
        /// production-sized database takes minutes, and ping rounds go on meanwhile rather than
        /// leaving a gap in the history. Everything a service start would do to the file is done to
        /// it there, and its settings are read there, so that once it is in place nothing is left
        /// to fail. Only the swap holds the gate: one rename of the candidate over the database,
        /// after which the settings read from it go into force from memory. No copy of the database
        /// it replaces is kept, even for that moment.
        /// </para>
        /// <para>
        /// Every failure is an <see cref="IOException"/>, and every one comes before the swap, with
        /// nothing replaced: a backup that cannot be migrated or lacks part of the schema, one whose
        /// settings will not read, or a database that stays in use.
        /// </para>
        /// <para>
        /// Runs on the thread pool, for the reason <see cref="ValidateBackupAsync"/> gives: an old
        /// backup's migration would otherwise hold the page asking for minutes.
        /// </para>
        /// <para>
        /// Checks no password. <see cref="RestoreUploadAsync"/>, which is how a page restores, asks
        /// for the backup's before it goes on to what this does.
        /// </para>
        /// <para>
        /// A size limit in the backup that this disk cannot safely hold, or none, is lowered to the
        /// safe limit in the backup itself before the swap, so that it is in force from the first
        /// round; what it was and what it became come back.
        /// </para>
        /// </summary>
        /// <returns>The backup's size limit and what it was lowered to, or null where it fit.</returns>
        internal Task<SizeLimitLowered?> RestoreAsync(string candidatePath, CancellationToken cancellationToken = default) =>
            Task.Run(
                async () =>
                {
                    if (!TryTakeTurn(RestoreUnderWay, out var refusal))
                    {
                        throw new IOException(refusal);
                    }

                    try
                    {
                        return await RestoreOnThisThreadAsync(candidatePath, cancellationToken);
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
        /// asks for the password it puts in force, if it puts one in force, and puts it in place as
        /// <see cref="RestoreAsync"/> does, all in the one turn, so that nothing else of this class
        /// writes beside the database from the room check to the swap.
        /// <paramref name="progress"/> hears which part is under way, and how much of the upload has
        /// arrived while it arrives.
        /// </summary>
        /// <param name="askPassword">
        /// Asks whoever restores the backup for its password, and gives what they answered. It is
        /// handed the sentence saying why the last answer was refused, null the first time, and a
        /// token that says to stop waiting. A backup that puts a password in force is only restored
        /// with that password, so whoever restores it can unlock HostPinger afterwards; a wrong
        /// answer is asked about again, as often as it takes, with the file already here. Not called
        /// for a backup that puts no password in force, or for a file that is refused.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancelled is how whoever cannot give the password leaves: the upload is deleted, the turn
        /// given back and nothing replaced, and this ends in an
        /// <see cref="OperationCanceledException"/>.
        /// </param>
        /// <returns>
        /// Once the backup is in force, no refusal, and the backup's size limit and what it was
        /// lowered to where this disk could not safely hold it. Otherwise why nothing was replaced:
        /// a snapshot or another restore under way, too little room beside the database for the
        /// file, a refusal of the file itself, or a password that was not given in time.
        /// </returns>
        /// <remarks>
        /// Every failure is an <see cref="IOException"/>, as <see cref="RestoreAsync"/>'s are,
        /// except whatever reading <paramref name="source"/> throws, which comes out as it is: for a
        /// page, the browser failing to read the file it chose. However it ends, the file received
        /// does not outlive this call.
        /// </remarks>
        public async Task<RestoreResult> RestoreUploadAsync(
            Stream source,
            long length,
            Func<string?, CancellationToken, Task<string>> askPassword,
            IProgress<RestoreProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!TryTakeTurn(RestoreUnderWay, out var refusal))
            {
                return new RestoreResult(refusal);
            }

            try
            {
                if (RoomRefusal(length, "Receiving the backup") is { } noRoom)
                {
                    return new RestoreResult(noRoom);
                }

                var candidatePath = CreateRestoreTempPath();
                try
                {
                    progress?.Report(new RestoreProgress(RestoreStep.Receiving, 0));
                    await ReceiveAsync(source, candidatePath, length, progress, cancellationToken);

                    progress?.Report(new RestoreProgress(RestoreStep.Checking, null));
                    if (await ValidateBackupAsync(candidatePath, cancellationToken) is { } invalid)
                    {
                        return new RestoreResult(invalid);
                    }

                    // After the file's own checks, so that nobody is asked for the password of a file
                    // that they would turn away.
                    if (await PasswordRefusalAsync(candidatePath, askPassword, cancellationToken) is { } notGiven)
                    {
                        return new RestoreResult(notGiven);
                    }

                    progress?.Report(new RestoreProgress(RestoreStep.Replacing, null));
                    var lowered = await Task.Run(() => RestoreOnThisThreadAsync(candidatePath, cancellationToken), CancellationToken.None);
                    return new RestoreResult(Refusal: null, lowered);
                }
                finally
                {
                    // The swap consumes the file; this catches every earlier exit — an upload that
                    // failed, a file refused, a password not given, a restore that replaced nothing
                    // — and what SQLite left beside it. A backup copied off a live database is in
                    // WAL mode, and the read-only connections that check one leave a -wal and -shm
                    // behind them.
                    DeleteTempFile(candidatePath);
                }
            }
            finally
            {
                ReleaseTurn();
            }
        }

        /// <summary>
        /// Null once <paramref name="askPassword"/> has given the password the backup at
        /// <paramref name="candidatePath"/> puts in force, which it is asked for until it does, or
        /// straight away when the backup puts none in force. Otherwise the refusal that says it was
        /// not given in time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The turn stays taken and the upload stays on the disk through the wait, which is why it
        /// is bounded, by <see cref="PasswordWait"/> for each answer: somebody still trying gets that
        /// long again. Nothing else waits on it. The ping rounds and the pages only stop for the
        /// swap, which is yet to come, so only another backup or restore is turned away meanwhile.
        /// </para>
        /// <para>
        /// A wrong password is not counted against the address it came from, as a guess at the
        /// current password is: the hash it is checked against is the uploaded file's own, which
        /// whoever holds the file can try passwords against without asking HostPinger. A backup
        /// without a password row is checked against this install's configured password instead,
        /// which is either the one in force, which the page asks for before a restore, or one a
        /// stored row has overridden.
        /// </para>
        /// <para>
        /// Checked on the thread pool, because checking a password is a tenth of a second of
        /// hashing, or up to a second for a hash that a backup brings with a higher cost.
        /// </para>
        /// </remarks>
        private async Task<string?> PasswordRefusalAsync(
            string candidatePath,
            Func<string?, CancellationToken, Task<string>> askPassword,
            CancellationToken cancellationToken)
        {
            var hash = await ReadPasswordHashAsync(candidatePath, cancellationToken);
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            string? refused = null;
            while (true)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waiting.CancelAfter(PasswordWait);
                string password;
                try
                {
                    password = await askPassword(refused, waiting.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return PasswordNotGiven;
                }

                if (await Task.Run(() => PasswordHash.Verify(hash, password), cancellationToken))
                {
                    return null;
                }

                refused = PasswordWrong;
            }
        }

        /// <summary>
        /// The hash of the password the backup at <paramref name="candidatePath"/> puts in force,
        /// read the way its settings will be loaded once it is in place: its own password row, an
        /// empty one meaning none, and without one this install's configured password.
        /// </summary>
        /// <remarks>
        /// Read before the backup is migrated, so that a mistyped password does not cost an old
        /// backup's migration first, which takes minutes on a production-sized file. The migrations
        /// create the settings table in a backup older than it and otherwise leave its rows as they
        /// are, so the password read here is the one the restore puts in force. On the thread pool,
        /// for the reason <see cref="ValidateBackupAsync"/> gives.
        /// </remarks>
        private Task<string?> ReadPasswordHashAsync(string candidatePath, CancellationToken cancellationToken) =>
            Task.Run<string?>(
                async () =>
                {
                    try
                    {
                        // Read-only and out of the pool, as validation's connection is, so that
                        // nothing holds the candidate open across the swap.
                        var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                            .UseSqlite(new SqliteConnectionStringBuilder
                            {
                                DataSource = candidatePath,
                                Mode = SqliteOpenMode.ReadOnly,
                                Pooling = false,
                            }.ToString())
                            .Options;
                        await using var db = new HostPingerDbContext(options);
                        return await settings.PasswordHashInAsync(db, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A settings table that cannot be read the way the restore would load it,
                        // which the restore would only have failed on later.
                        throw new IOException($"{ex.Message} Nothing was replaced.", ex);
                    }
                },
                cancellationToken);

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

        private async Task<SizeLimitLowered?> RestoreOnThisThreadAsync(string candidatePath, CancellationToken cancellationToken)
        {
            var (lowered, settingsRows) = await PrepareCandidateAsync(candidatePath, cancellationToken);

            using (await maintenanceGate.AcquireAsync(cancellationToken))
            {
                // The host summary keeps a connection of its own open, outside the pool that
                // ClearAllPools empties, so it lets go of the file first and is resumed on whatever
                // sits at the path afterwards — the restored file, or the old one if the swap never
                // happened.
                await hostSummaries.SuspendAsync(cancellationToken);
                try
                {
                    // The pages' reads and writes take no turns at the gate above. The file gate
                    // waits for the connections they have open, and keeps new ones back through
                    // everything that follows: emptying the pool, the swap, and putting the
                    // backup's settings in force.
                    await fileGate.WhileShutAsync(
                        TimeSpan.FromSeconds(BusyTimeoutSeconds),
                        () => ReplaceAsync(candidatePath, settingsRows, cancellationToken),
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

            if (lowered is { } limit)
            {
                logger.LogWarning(
                    "The restored backup's size limit, {From} MB, is more than this disk can safely hold, so it is {To} MB now. "
                    + "{Over} bytes of its history are over that, and the ping rounds prune them from the oldest.",
                    limit.FromMb,
                    limit.ToMb,
                    limit.OverBytes);
            }

            return lowered;
        }

        /// <summary>
        /// Brings the candidate up to date the way a service start brings up a database
        /// (<see cref="HostPingerDatabase.InitializeAsync"/>) and checks it has every table and
        /// column this build reads, lowers a size limit in it that this disk cannot safely hold
        /// (see <see cref="FitSizeLimitAsync"/>), reads the settings it puts in force, then puts it
        /// in WAL mode, all on connections of its own that are closed again before the swap. So a
        /// file that would not start the service, or whose settings will not read, is refused
        /// here, with nothing replaced, rather than found out once it is in place. Validation reads
        /// the migration history, not the tables it vouches for, and a file can carry the one
        /// without the others; checked after the migrations, because an older backup is only
        /// complete after them.
        /// </summary>
        private async Task<(SizeLimitLowered? Lowered, Dictionary<string, string> SettingsRows)> PrepareCandidateAsync(
            string candidatePath,
            CancellationToken cancellationToken)
        {
            SizeLimitLowered? lowered;
            Dictionary<string, string> settingsRows;
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

                    lowered = await FitSizeLimitAsync(db, cancellationToken);
                    settingsRows = await UserSettingsStore.StoredInAsync(db, cancellationToken);
                }

                await EnterWalAsync(candidatePath, BusyTimeoutSeconds, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A migration that fails on the file, a disk that fills partway through it: the
                // live database has not been touched, which is what the reader needs to know.
                throw new IOException($"{ex.Message} Nothing was replaced.", ex);
            }

            return (lowered, settingsRows);
        }

        /// <summary>
        /// Lowers the size limit the candidate in <paramref name="db"/> puts in force to the safe
        /// limit for this disk where it is more than that, or none: the limit came from the install
        /// the backup was taken on, whose disk may have been bigger, and kept, it would let the
        /// restored database grow until this disk was full. Written into the candidate, so that it
        /// is in force from the first round after the swap.
        /// </summary>
        /// <remarks>
        /// Measured against the room the database will have once the restore is done: what is free
        /// now, the database being replaced, which is deleted after the swap, and the candidate,
        /// which becomes the database. A candidate already larger than the limit it is given keeps
        /// all of its history through the restore, and the ping rounds after it prune the oldest of
        /// it down to the limit, a slice a round.
        /// </remarks>
        /// <returns>What the limit was and what it is now, or null where it fit.</returns>
        private async Task<SizeLimitLowered?> FitSizeLimitAsync(HostPingerDbContext db, CancellationToken cancellationToken)
        {
            var limitMb = (await settings.PingerInAsync(db, cancellationToken)).MaxDatabaseSizeMb;
            var candidateBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db, cancellationToken);
            var safeMb = DatabaseSizeLimit.SafeLimitMbFor(
                MeasureFreeBytes() + DatabaseSizeLimit.FootprintBytes(paths.DatabasePath) + candidateBytes);
            if (limitMb > 0 && limitMb <= safeMb)
            {
                return null;
            }

            await UserSettingsStore.StageAsync(
                db, UserSettingsStore.MaxDatabaseSizeMbKey, safeMb.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new SizeLimitLowered(limitMb, safeMb, Math.Max(0, candidateBytes - ((long)safeMb * 1024 * 1024)));
        }

        /// <summary>
        /// Moves the candidate over the database and puts the settings read from it in force. The
        /// candidate was brought up to date, checked and read where it lay, so nothing after the
        /// move can find it wanting, and there is nothing to put back.
        /// </summary>
        private async Task ReplaceAsync(
            string candidatePath,
            Dictionary<string, string> settingsRows,
            CancellationToken cancellationToken)
        {
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

                // A journal left beside the database belongs to the file being replaced. SQLite
                // would roll it into whatever sits at the database path, and rolled into the
                // restored file it is corruption, not recovery.
                DeleteSidecars();

                if (BeforeReplacing is { } beforeReplacing)
                {
                    await beforeReplacing();
                }

                await SwapIntoPlaceAsync(candidatePath, cancellationToken);
            }
            catch (SqliteException ex)
            {
                // A file SQLite will not work on is a failed restore to report, like the file kept
                // busy above.
                throw new IOException($"{ex.Message} Nothing was replaced.", ex);
            }

            // From here the restore is done, whatever becomes of the page that asked for it. Until
            // the settings read from the backup go in, the ones in force describe the file just
            // replaced; the backup's password going in also invalidates unlock cookies stamped
            // under the old one, with nothing further to do.
            if (AfterSwap is { } afterSwap)
            {
                await afterSwap();
            }

            settings.PutInForce(settingsRows);
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

        private void DeleteSidecars()
        {
            File.Delete(paths.DatabasePath + "-journal");
            File.Delete(paths.DatabasePath + "-wal");
            File.Delete(paths.DatabasePath + "-shm");
        }

        /// <summary>
        /// Takes the file about to be replaced out of WAL mode. SQLite only leaves it once no other
        /// connection has the file open, so this is what waits out a read still in progress on it,
        /// for as long as the provider retries a busy command, and leaving it folds whatever the
        /// -wal still holds into the file, which leaves nothing of it beside the database. The
        /// application's own connections have been waited for at the <see cref="DatabaseFileGate"/>
        /// by now, so what is left to wait for here is a connection from outside it, another
        /// process's. That wait is what keeps the restored file safe: the last connection to close
        /// on a WAL database deletes the -wal and -shm beside it by name, and after the swap those
        /// names are the restored file's. A file already in rollback-journal mode has neither, and
        /// is left as it is.
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
        /// One atomic rename of the candidate over the database, so a crash at any moment leaves
        /// the database or the backup at the path, never neither, and no copy of the one it
        /// replaced: the old file's space goes with the rename. Retried briefly because a page's
        /// periodic read can hold the file at just the wrong instant on Windows. A
        /// hostpinger.db.pre-restore that an earlier version's restore kept beside the database is
        /// left alone.
        /// </summary>
        private async Task SwapIntoPlaceAsync(string candidatePath, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(candidatePath, paths.DatabasePath, overwrite: true);
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

        /// <summary>Deletes a temp file and whatever SQLite keeps beside it.</summary>
        private static void DeleteTempFile(string path)
        {
            foreach (var suffix in TempFileSuffixes)
            {
                File.Delete(path + suffix);
            }
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

    /// <summary>What came of a restore from an upload.</summary>
    /// <param name="Refusal">
    /// Why nothing was replaced, as the sentence to show whoever sent the upload; null once the
    /// backup is in force.
    /// </param>
    /// <param name="LimitLowered">
    /// Once the backup is in force: the size limit it brought and what that was lowered to, where
    /// it was more than this disk can safely hold, or none. Null where it fit.
    /// </param>
    public sealed record RestoreResult(string? Refusal, SizeLimitLowered? LimitLowered = null);

    /// <summary>A restored backup's size limit, lowered to what this disk can safely hold.</summary>
    /// <param name="FromMb">The limit the backup put in force: zero or less for none.</param>
    /// <param name="ToMb">The limit in force now, the safe limit for this disk with the backup in place.</param>
    /// <param name="OverBytes">
    /// How much of the restored file is over the new limit: the oldest history, which the ping
    /// rounds prune a slice a round.
    /// </param>
    public sealed record SizeLimitLowered(int FromMb, int ToMb, long OverBytes);
}
