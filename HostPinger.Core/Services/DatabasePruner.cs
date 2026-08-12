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
        /// One statement per table that holds recorded history, each deleting a batch of that
        /// table's oldest rows. The hosts and the settings are not history and are never deleted to
        /// make room.
        /// </summary>
        private static readonly string[] DeleteOldestStatements =
        [
            "DELETE FROM PingAttempts WHERE Id IN (SELECT Id FROM PingAttempts ORDER BY TimestampUtc LIMIT {0})",
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
        /// including the zero that disables size pruning, because that setting is about how much
        /// disk the file may take rather than about keeping what cannot be read.
        /// </remarks>
        public async Task<int> EnforceResolverErrorRetentionAsync(
            HostPingerDbContext db,
            DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            // Computed here rather than inside the predicate so the comparison is against a plain
            // parameter, which seeks IX_ResolverErrors_TimestampUtc.
            var cutoffUtc = nowUtc - ResolverError.Retention;
            var deleted = await db.ResolverErrors
                .Where(e => e.TimestampUtc < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0)
            {
                await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken);
            }

            return deleted;
        }

        /// <summary>Returns the number of rows deleted, across every history table.</summary>
        public async Task<int> EnforceSizeLimitAsync(HostPingerDbContext db, long maxSizeBytes, CancellationToken cancellationToken = default)
        {
            if (maxSizeBytes <= 0)
            {
                return 0;
            }

            var totalDeleted = 0;
            while (await GetDatabaseSizeBytesAsync(db, cancellationToken) > maxSizeBytes)
            {
                // Every table is trimmed on each pass rather than one being emptied before the next
                // is touched, because either can be the one filling the file: an address that never
                // resolves records an error every round for as long as it stays configured. A limit
                // enforced against the attempts alone would be held above it by the errors, with
                // nothing left to delete and no way back under the limit.
                var deleted = 0;
                foreach (var statement in DeleteOldestStatements)
                {
                    deleted += await db.Database.ExecuteSqlRawAsync(
                        statement,
                        new object[] { DeleteBatchSize },
                        cancellationToken);
                }

                if (deleted == 0)
                {
                    break;
                }

                totalDeleted += deleted;
                await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken);
            }

            return totalDeleted;
        }

        public static async Task<long> GetDatabaseSizeBytesAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
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
