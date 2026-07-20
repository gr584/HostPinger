using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    internal static class TestDb
    {
        /// <summary>
        /// Creates an in-memory SQLite database that lives as long as the returned connection.
        /// Every context created from the options shares that connection.
        /// </summary>
        public static (SqliteConnection Connection, DbContextOptions<HostPingerDbContext> Options) CreateInMemory()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite(connection)
                .Options;
            using var db = new HostPingerDbContext(options);
            db.Database.EnsureCreated();
            return (connection, options);
        }

        internal sealed class Factory(DbContextOptions<HostPingerDbContext> options) : IDbContextFactory<HostPingerDbContext>
        {
            public HostPingerDbContext CreateDbContext() => new(options);
        }
    }
}
