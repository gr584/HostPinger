using System.Diagnostics;
using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Keeps the SQLite database file under a configured size by deleting the oldest recorded
    /// history. Requires incremental auto-vacuum (see <see cref="HostPingerDatabase"/>) so the
    /// file actually shrinks after deletes.
    /// </summary>
    public class DatabasePruner
    {
        private const int DeleteBatchSize = 1000;

        /// <summary>
        /// Run once <see cref="DeleteOldestAsync"/> has deleted its batch. For the tests, which have
        /// no other way to have a delete take a page of its own, as one that splits an index's
        /// interior page now and then does.
        /// </summary>
        internal Func<HostPingerDbContext, Task>? AfterDeletingOldest { get; init; }

        /// <summary>
        /// One statement per table that holds recorded history, each deleting a batch of that
        /// table's oldest rows. The hosts and the settings are not history and are never deleted to
        /// make room.
        /// </summary>
        /// <remarks>
        /// The attempts are ordered by Id, which is the same order as by TimestampUtc: a round
        /// stamps one time on every attempt it records (see <c>PingMonitorService</c>) and the ids
        /// are handed out as those rows go in, so ids ascend with rounds and rounds ascend with the
        /// clock. Reading them in the key they are already stored in makes the oldest batch the
        /// first thousand rows of the table, which costs nothing to find, and it is what lets the
        /// attempts carry no index on TimestampUtc alone — that index was a third of the database
        /// and this statement was the only thing that ever read it. Insertion order also survives a
        /// clock that steps backwards, where it still says which rows arrived first and the
        /// timestamps no longer do.
        /// <para>
        /// The resolver errors stay on TimestampUtc. That table indexes it for the retention sweep
        /// above regardless, so ordering by it here is already free.
        /// </para>
        /// </remarks>
        private static readonly string[] DeleteOldestStatements =
        [
            "DELETE FROM PingAttempts WHERE Id IN (SELECT Id FROM PingAttempts ORDER BY Id LIMIT {0})",
            "DELETE FROM ResolverErrors WHERE Id IN (SELECT Id FROM ResolverErrors ORDER BY TimestampUtc LIMIT {0})",
        ];

        /// <summary>
        /// Deletes resolver errors older than <see cref="ResolverError.Retention"/>, and returns
        /// how many went. Returned space is reclaimed only when there was something to reclaim, so
        /// an ordinary round — which is every round, since almost nothing ever ages out on any one
        /// of them — costs one indexed delete that matches no rows.
        /// </summary>
        /// <remarks>
        /// Age rather than size, and so unconditional: the resolver errors page counts over thirty
        /// days and nothing reads past it, so rows older than that are not history being kept for
        /// anyone — they are rows no page can show. This runs whatever the size limit is set to,
        /// because that setting is about how much disk the file may take rather than about keeping
        /// what cannot be read.
        /// </remarks>
        /// <param name="db">The context to delete through.</param>
        /// <param name="nowUtc">The moment the retention is counted back from.</param>
        /// <param name="reclaimSpace">
        /// Whether to give the space they took back to the disk. Not while the disk is nearly full,
        /// when the round holds the database at its size and writes into that space instead (see
        /// <see cref="DatabaseRoom.IsDiskNearlyFull"/>): it would otherwise be given back, and the
        /// history deleted to make the same room again.
        /// </param>
        /// <param name="cancellationToken">Cancels the delete.</param>
        public async Task<int> EnforceResolverErrorRetentionAsync(
            HostPingerDbContext db,
            DateTime nowUtc,
            bool reclaimSpace = true,
            CancellationToken cancellationToken = default)
        {
            // Computed here rather than inside the predicate so the comparison is against a plain
            // parameter, which seeks IX_ResolverErrors_TimestampUtc.
            var cutoffUtc = nowUtc - ResolverError.Retention;
            var deleted = await db.ResolverErrors
                .Where(e => e.TimestampUtc < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0 && reclaimSpace)
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken);
            }

            return deleted;
        }

        /// <summary>
        /// Deletes the oldest history a batch at a time until the file is back under
        /// <paramref name="maxSizeBytes"/>, or until <paramref name="budget"/> has been spent, and
        /// returns the number of rows deleted across every history table. Whatever the budget leaves
        /// over is the next call's to delete.
        /// </summary>
        /// <remarks>
        /// The budget is what keeps a lowered limit from stopping the monitoring. A round holds the
        /// maintenance gate while it prunes, and on the benchmarks' production-shaped database the
        /// pruning goes at about 2.5 MB a second, so a limit lowered by a gigabyte took the next
        /// round about seven minutes, with no round anywhere in them. At the limit, one round's
        /// growth takes a batch or two, and the budget never comes into it. The clock is looked at
        /// once a batch is done, so a call always gets at least one in, whatever its budget.
        /// </remarks>
        internal async Task<int> EnforceSizeLimitAsync(
            HostPingerDbContext db,
            long maxSizeBytes,
            TimeSpan budget,
            CancellationToken cancellationToken = default)
        {
            // No limit in force is zero or less once the service has started (see
            // DatabaseSizeLimit.SettleAsync). One that somehow were would delete nothing, rather
            // than everything.
            if (maxSizeBytes <= 0)
            {
                return 0;
            }

            var started = Stopwatch.GetTimestamp();
            var totalDeleted = 0;
            while (await GetDatabaseSizeBytesAsync(db, cancellationToken) > maxSizeBytes)
            {
                var deleted = await DeleteOldestAsync(db, cancellationToken);
                if (deleted == 0)
                {
                    break;
                }

                totalDeleted += deleted;
                await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken);
                if (Stopwatch.GetElapsedTime(started) >= budget)
                {
                    break;
                }
            }

            return totalDeleted;
        }

        /// <summary>
        /// Deletes a batch of the oldest rows of every history table, and returns how many went.
        /// The pages they took stay inside the file, free for whatever is written next, until a
        /// vacuum gives them back to the disk.
        /// </summary>
        /// <remarks>
        /// Every table is trimmed on each pass rather than one being emptied before the next is
        /// touched, because either can be the one filling the file: an address that never resolves
        /// records an error every round for as long as it stays configured. A limit enforced
        /// against the attempts alone would be held above it by the errors, with nothing left to
        /// delete and no way back under the limit.
        /// </remarks>
        internal async Task<int> DeleteOldestAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
        {
            var deleted = 0;
            foreach (var statement in DeleteOldestStatements)
            {
                deleted += await db.Database.ExecuteSqlRawAsync(
                    statement,
                    new object[] { DeleteBatchSize },
                    cancellationToken);
            }

            if (AfterDeletingOldest is { } afterDeleting)
            {
                await afterDeleting(db);
            }

            return deleted;
        }

        internal static async Task<long> GetDatabaseSizeBytesAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result);
        }
    }
}
