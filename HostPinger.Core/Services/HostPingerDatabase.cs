using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Prepares the SQLite database: says how to open it, applies migrations, enables
    /// incremental auto-vacuum so pruning can shrink the file, puts it in WAL mode, and loads what
    /// the application reads from it before it serves a page.
    /// </summary>
    /// <remarks>
    /// The web project used to do the opening and the ordering itself, building the connection
    /// string in two places — the service registration and the --remove-password command — and
    /// keeping the order of the startup steps in comments in Program. Here they are next to the
    /// services they order, and tested.
    /// </remarks>
    public static class HostPingerDatabase
    {
        /// <summary>
        /// The connection string for the database at <paramref name="databasePath"/>. Built rather
        /// than written out, so that a path with a semicolon or a quote in it names that file
        /// instead of breaking the string apart.
        /// </summary>
        internal static string ConnectionString(string databasePath) =>
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        /// <summary>
        /// The most a -wal file is left holding once a checkpoint has copied it into the database.
        /// Left alone, SQLite keeps a -wal at the largest size it ever reached until the last
        /// connection to the file closes, and the host summary's never does while the service
        /// runs: one large write — deleting a host with months of history, migrating a big
        /// database — would leave hundreds of megabytes beside the file, which the size limit does
        /// not count, until the next restart.
        /// </summary>
        public const long JournalSizeLimitBytes = 64L * 1024 * 1024;

        /// <summary>Options for a context over the database at <paramref name="databasePath"/>.</summary>
        internal static DbContextOptions<HostPingerDbContext> Options(string databasePath) =>
            new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite(ConnectionString(databasePath))
                .AddInterceptors(JournalSizeLimit.Instance)
                .Options;

        /// <summary>
        /// Registers the context factory every reader and writer of the database takes its contexts
        /// from, over the database <paramref name="paths"/> names.
        /// </summary>
        public static IServiceCollection AddHostPingerDatabase(this IServiceCollection services, PingerPaths paths) =>
            services.AddDbContextFactory<HostPingerDbContext>(
                options => options
                    .UseSqlite(ConnectionString(paths.DatabasePath))
                    .AddInterceptors(JournalSizeLimit.Instance));

        /// <summary>
        /// What has to happen before the first page is served, from the services
        /// <see cref="AddHostPingerDatabase"/> and the application registered; see the overload
        /// taking them, which is where the order is kept.
        /// </summary>
        public static Task OpenAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            OpenAsync(
                services.GetRequiredService<IDbContextFactory<HostPingerDbContext>>(),
                services.GetRequiredService<UserSettingsStore>(),
                services.GetRequiredService<HostSummaryCache>(),
                cancellationToken);

        /// <summary>
        /// Brings the database up to date, then loads the settings from it, then the host summary.
        /// </summary>
        /// <remarks>
        /// The order is the point. The migrations come first, because they are what guarantee the
        /// tables the other two read exist — a database an older build left has no settings table
        /// yet. The settings come before the summary, whose statuses depend on the retry count:
        /// read the other way round, a stored count would not be in force for the first page drawn.
        /// And all of it before the first page is served, so that no page ever draws the summary
        /// empty.
        /// </remarks>
        internal static async Task OpenAsync(
            IDbContextFactory<HostPingerDbContext> dbFactory,
            UserSettingsStore settings,
            HostSummaryCache summaries,
            CancellationToken cancellationToken = default)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                await InitializeAsync(db, cancellationToken);
            }

            await settings.LoadAsync(cancellationToken);
            await summaries.LoadAsync(cancellationToken);
        }

        /// <summary>
        /// Brings the database up to date: incremental auto-vacuum, then the migrations, after
        /// clearing any lock a migration stopped partway through left behind, then WAL mode.
        /// </summary>
        /// <returns>
        /// Whether there was such a lock to clear, which is also logged as a warning through the
        /// context's own logging: the one trace left of a start, restore or --remove-password that
        /// was stopped while it migrated.
        /// </returns>
        internal static async Task<bool> InitializeAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await EnableIncrementalVacuumAsync(db, cancellationToken);
                var cleared = await ClearMigrationLockAsync(db, cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
                await EnterWalAsync(db, cancellationToken);
                return cleared;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        /// <summary>
        /// Deletes the row EF Core's migrator holds as its lock, if a migrator that never finished
        /// left one behind, and says whether it did.
        /// </summary>
        /// <remarks>
        /// The migrator locks a SQLite database by inserting a row into __EFMigrationsLock, and only
        /// deletes it again once it is done. A process stopped partway through leaves the row in
        /// place: a start that ran past systemd's start timeout while a migration built an index
        /// over a large history, a reboot, a kill. Every migrator after it then waits on that row
        /// forever — the service never starts, and neither --remove-password nor a restore gets past
        /// it — until somebody deletes it by hand.
        /// <para>
        /// Deleting it unasked is safe because nothing else migrates this database while this runs.
        /// The service migrates before it serves anything, and a restore migrates inside the
        /// service with the maintenance gate held. The one other process that migrates is
        /// --remove-password, which is meant to be run with the service stopped. Run against a
        /// service still migrating regardless, the delete is a write like any other: it waits
        /// behind the migration under way, which holds the file's write lock until it commits, and
        /// gives up with "database is locked" once the busy timeout runs out. Should it slip in
        /// between two migrations instead, each one commits together with its history row, so a
        /// second attempt at one the other has just applied fails on finding its work done, and
        /// rolls back, rather than applying it twice.
        /// </para>
        /// </remarks>
        private static async Task<bool> ClearMigrationLockAsync(HostPingerDbContext db, CancellationToken cancellationToken)
        {
            // The table is EF's, created the first time a migrator takes the lock; a brand-new file
            // has none to clear.
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsLock';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0
                || await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsLock\";", cancellationToken) == 0)
            {
                return false;
            }

            LoggerFor(db).LogWarning(
                    "Cleared the lock an interrupted migration left in {Database}: a start, restore or "
                    + "--remove-password was stopped partway through migrating it. That migration runs again now.",
                    connection.DataSource);
            return true;
        }

        /// <summary>
        /// Puts a database still in rollback-journal mode into WAL mode, where a read never holds up
        /// a write. In rollback-journal mode a ping round that comes due during a long read waits
        /// for it, and past the busy timeout gives up and loses its pings. A new database starts in
        /// rollback-journal mode — the file is opened here before EF sees it, so EF never creates
        /// it, which is when EF would choose WAL — and an older build left its database in it, so
        /// this is what moves both; a restore puts its file in WAL mode itself.
        /// </summary>
        /// <remarks>
        /// After the migrations, so that an upgrade's index rebuild is journalled the old way and
        /// leaves nothing behind once it commits, rather than growing a -wal to the size of the
        /// index. A file another connection is holding keeps its mode, with a warning, rather than
        /// stopping the start: the next start switches it. Where SQLite cannot use WAL on the file,
        /// it answers with the mode it kept, and the database goes on in that.
        /// </remarks>
        private static async Task EnterWalAsync(HostPingerDbContext db, CancellationToken cancellationToken)
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            if (await command.ExecuteScalarAsync(cancellationToken) is "wal")
            {
                return;
            }

            command.CommandText = "PRAGMA journal_mode = WAL;";
            try
            {
                await command.ExecuteScalarAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                // SQLITE_BUSY and SQLITE_LOCKED, past the provider's wait.
                LoggerFor(db).LogWarning(
                    ex,
                    "{Database} stays in rollback-journal mode for now: another connection held it. The next start puts it in WAL mode.",
                    connection.DataSource);
            }
        }

        /// <summary>
        /// The context's own logging, which for one from the application's factory is the
        /// application's: the service and a restore log through it without being handed a logger.
        /// </summary>
        private static ILogger LoggerFor(HostPingerDbContext db) =>
            db.GetService<ILoggerFactory>().CreateLogger(typeof(HostPingerDatabase).FullName!);

        /// <summary>
        /// The auto_vacuum pragma only takes effect on a brand-new database file or after VACUUM
        /// rebuilds an existing one, and the connection must stay open between statements.
        /// </summary>
        internal static async Task EnableIncrementalVacuumAsync(HostPingerDbContext db, CancellationToken cancellationToken = default)
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
