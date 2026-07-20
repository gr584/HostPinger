using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Prepares the SQLite database: applies migrations and enables incremental auto-vacuum so
    /// pruning can shrink the file.
    /// </summary>
    public static class HostPingerDatabase
    {
        public static async Task InitializeAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await EnableIncrementalVacuumAsync(db, cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        /// <summary>
        /// The auto_vacuum pragma only takes effect on a brand-new database file or after VACUUM
        /// rebuilds an existing one, and the connection must stay open between statements.
        /// </summary>
        public static async Task EnableIncrementalVacuumAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA auto_vacuum = INCREMENTAL;", cancellationToken);
            if (await GetAutoVacuumModeAsync(db, cancellationToken) != 2)
            {
                await db.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
            }
        }

        private static async Task<long> GetAutoVacuumModeAsync(HostPingerDbContext db, CancellationToken cancellationToken)
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA auto_vacuum;";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result);
        }
    }
}
