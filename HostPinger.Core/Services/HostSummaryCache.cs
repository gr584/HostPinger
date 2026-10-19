using HostPinger.Core.Data;
using HostPinger.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// What the Hosts page draws — every host with its latest ping, last downtime and status — held
    /// in memory and read again by this service's own loop whenever the database changes, rather
    /// than by every page that shows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The summary is one query, and every open Hosts page ran it every five seconds, twice more to
    /// open the page and once again for each sort: a cost that grew with the screens showing it, for
    /// data that only changes when a ping round lands or a host is edited. Here it is read once per
    /// change however many pages show it, and a page drawing it, prerendered or live, never waits on
    /// the database at all.
    /// </para>
    /// <para>
    /// A change is noticed through SQLite's data_version, asked once a second on a connection this
    /// service keeps open: it moves whenever any other connection has committed since this one last
    /// asked. That covers every writer alike — the ping rounds and the pruner, a host edited on the
    /// page, and writers outside the process such as the browser tests, which seed rows while the
    /// application runs — without any of them having to say so, and while nothing moves it costs a
    /// pragma a second. Nothing is ever written through the connection, which matters: data_version
    /// does not move for commits made on the connection asking.
    /// </para>
    /// <para>
    /// RetryAttempts changes what the same rows mean, so a change to it is a reason to read again as
    /// well. <see cref="HostStore"/>, having changed a host, asks for a read and waits for it, see
    /// <see cref="RefreshAsync"/>, so that the summary it hands the page that made the change
    /// already includes it rather than catching up a second later.
    /// </para>
    /// <para>
    /// The connection holds the file open outside the pool, where a restore's ClearAllPools cannot
    /// reach it, so a restore suspends this before swapping the file and resumes it afterwards, as it
    /// reloads the settings. Reading needs the database file to be a file: a context configured with
    /// an in-memory connection gives this nothing to open a second connection to.
    /// </para>
    /// </remarks>
    public sealed class HostSummaryCache(
        IDbContextFactory<HostPingerDbContext> dbFactory,
        UserSettingsStore settings,
        ILogger<HostSummaryCache> logger) : BackgroundService
    {
        /// <summary>How often the loop asks whether the database has changed.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        // The open connection is used by one thing at a time: a read, or suspending.
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        // Released to wake the loop before its second is up, when HostStore or Resume asks for a read.
        private readonly SemaphoreSlim _wake = new(0, 1);

        // Guards the pending request and the wake-up.
        private readonly object _requestLock = new();
        private TaskCompletionSource<HostSummarySnapshot>? _requested;

        private SqliteConnection? _connection;
        private DbContextOptions<HostPingerDbContext>? _options;
        private long _dataVersion;
        private volatile bool _suspended;
        private volatile HostSummarySnapshot _current = new([], 1);
        private bool _failing;

        /// <summary>
        /// The summary as last read. Loaded at startup, before the first page is served, so a page
        /// never sees it empty for any reason but there being no hosts.
        /// </summary>
        public HostSummarySnapshot Current => _current;

        /// <summary>
        /// Raised with each summary newly read, on this service's loop rather than on any page's
        /// circuit, so a page hands it on to its own before drawing it.
        /// </summary>
        public event Action<HostSummarySnapshot>? Updated;

        /// <summary>
        /// Reads the summary whatever data_version says. Called once at startup, after the
        /// migrations and the settings, which give it a schema to read and a retry count to read it
        /// with.
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            if (await ReadAsync(force: true, cancellationToken) is { } read)
            {
                Publish(read);
            }
        }

        /// <summary>
        /// Has the loop look now rather than at its next second, and hands back the summary as it
        /// stands after the asking: read again if anything has been committed since the last read,
        /// which a change HostStore has just made always has, since it was made on a connection
        /// other than this one. A read that fails hands back the summary in hand instead, so the
        /// caller has a summary to hand on rather than an error to pass up; the loop says what went
        /// wrong and tries again. The loop is what answers, so while it is not running only
        /// cancelling ends the wait.
        /// </summary>
        public Task<HostSummarySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            Task<HostSummarySnapshot> read;
            lock (_requestLock)
            {
                _requested ??= new TaskCompletionSource<HostSummarySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                read = _requested.Task;
                if (_wake.CurrentCount == 0)
                {
                    _wake.Release();
                }
            }

            return read.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Closes the connection and stops reading, for a restore about to swap the file out from
        /// under it: the connection holds the file open, which on Windows is enough to fail the swap
        /// and anywhere else leaves this reading the file that was retired. Pages go on drawing the
        /// summary in hand meanwhile.
        /// </summary>
        public async Task SuspendAsync(CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                _suspended = true;
                await CloseAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Lets the loop read again, from whatever file now sits at the path; the first read opens a
        /// fresh connection to it and reads whatever data_version says.
        /// </summary>
        public void Resume()
        {
            _suspended = false;
            _ = RefreshAsync();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _wake.WaitAsync(PollInterval, stoppingToken);
                    await PollAsync(stoppingToken);
                    if (_failing)
                    {
                        _failing = false;
                        logger.LogInformation("Reading the host summary works again.");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Once per run of failures rather than once a second for as long as it lasts.
                    if (!_failing)
                    {
                        _failing = true;
                        logger.LogWarning(ex, "Reading the host summary failed; pages keep the one in hand until a read works.");
                    }
                }
            }
        }

        /// <summary>
        /// One turn of the loop: reads the summary if the database or the retry count has changed
        /// since the last read, hands it to the pages, and answers whoever asked for it.
        /// </summary>
        internal async Task PollAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<HostSummarySnapshot>? requested;
            lock (_requestLock)
            {
                requested = _requested;
                _requested = null;
            }

            HostSummarySnapshot? read;
            try
            {
                read = await ReadAsync(force: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                requested?.TrySetCanceled(cancellationToken);
                throw;
            }
            catch
            {
                requested?.TrySetResult(_current);
                throw;
            }

            if (read is not null)
            {
                Publish(read);
            }

            requested?.TrySetResult(read ?? _current);
        }

        /// <summary>
        /// Reads the summary when <paramref name="force"/> says to, or when data_version or the retry
        /// count has moved since the last read; null when neither has, and while suspended.
        /// </summary>
        private async Task<HostSummarySnapshot?> ReadAsync(bool force, CancellationToken cancellationToken)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (_suspended)
                {
                    return null;
                }

                if (_connection is null)
                {
                    await OpenAsync(cancellationToken);

                    // A fresh connection's data_version says nothing about what was read before it.
                    force = true;
                }

                // Asked before the read rather than after it, so that a commit landing in between
                // is read again on the next turn rather than taken as already read.
                var dataVersion = await DataVersionAsync(_connection!, cancellationToken);
                var retryAttempts = settings.CurrentPinger.RetryAttempts;
                var missesToDown = Math.Max(0, retryAttempts) + 1;
                if (!force && dataVersion == _dataVersion && missesToDown == _current.MissesToDown)
                {
                    return null;
                }

                await using var db = new HostPingerDbContext(_options!);
                var rows = await HostSummary.LoadAsync(db, retryAttempts, cancellationToken);
                _dataVersion = dataVersion;
                return new HostSummarySnapshot(rows, missesToDown);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task OpenAsync(CancellationToken cancellationToken)
        {
            string connectionString;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                connectionString = db.Database.GetConnectionString()
                    ?? throw new InvalidOperationException("The database context has no connection string to open.");
            }

            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            _connection = connection;
            _options = new DbContextOptionsBuilder<HostPingerDbContext>().UseSqlite(connection).Options;
        }

        private async Task CloseAsync()
        {
            if (_connection is { } connection)
            {
                _connection = null;
                _options = null;
                await connection.DisposeAsync();
            }
        }

        private static async Task<long> DataVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA data_version;";
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        /// <summary>
        /// Makes <paramref name="read"/> the summary in hand and tells the pages, one at a time, so a
        /// page that fails to take it cannot keep it from the rest.
        /// </summary>
        private void Publish(HostSummarySnapshot read)
        {
            _current = read;
            if (Updated is not { } updated)
            {
                return;
            }

            foreach (var handler in updated.GetInvocationList().Cast<Action<HostSummarySnapshot>>())
            {
                try
                {
                    handler(read);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "A page could not take the new host summary.");
                }
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _connection?.Dispose();
        }
    }

    /// <summary>One read of the summary: every host, by name, and the misses that made each one down.</summary>
    /// <param name="Rows">The hosts as <see cref="HostSummary.LoadAsync"/> reads them. Shared by every page drawing them,
    /// so ordered or filtered into a list of a page's own rather than changed in place.</param>
    /// <param name="MissesToDown">How many missed pings in a row the rows were read as making a host down.</param>
    public sealed record HostSummarySnapshot(IReadOnlyList<HostSummary> Rows, int MissesToDown);
}
