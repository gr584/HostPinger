using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Keeps the SQLite database file under a configured size by deleting the oldest ping
    /// attempts. Requires incremental auto-vacuum (see <see cref="HostPingerDatabase"/>) so the
    /// file actually shrinks after deletes.
    /// </summary>
    public class DatabasePruner
    {
        private const int DeleteBatchSize = 1000;

        /// <summary>Returns the number of ping attempts deleted.</summary>
        public async Task<int> EnforceSizeLimitAsync(HostPingerDbContext db, long maxSizeBytes, CancellationToken cancellationToken = default)
        {
            if (maxSizeBytes <= 0)
            {
                return 0;
            }

            var totalDeleted = 0;
            while (await GetDatabaseSizeBytesAsync(db, cancellationToken) > maxSizeBytes)
            {
                var deleted = await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM PingAttempts WHERE Id IN (SELECT Id FROM PingAttempts ORDER BY TimestampUtc LIMIT {0})",
                    new object[] { DeleteBatchSize },
                    cancellationToken);
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
