using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Keeps the application's connections off the database file while a restore swaps it. Every
    /// connection the context factory opens goes through this gate and holds a pass from opening
    /// until it closes. A restore shuts the gate: it waits for the passes to be given back, keeps
    /// new connections waiting until it opens the gate again, and has the file to itself in between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ping rounds take turns with a restore through <see cref="MaintenanceGate"/>, but the
    /// pages read and write without waiting for anything: a graph refreshing, a host being deleted,
    /// a setting saved, from this browser or another. Before this gate, all a restore could do about
    /// them was empty the connection pool and wait for SQLite to let the file leave WAL mode, which
    /// it does only once no other connection has the file open. A page that opened a connection
    /// during that wait put one back in the pool that went on holding the file, so the restore gave
    /// up after half a minute with the rounds held off throughout. And once the file had left WAL
    /// mode, nothing kept a page from opening it again before the swap. A write begun there keeps a
    /// journal under the live file's name, which after the swap sits beside the restored file, and
    /// the next connection to open it rolls that journal into it: a restored database silently
    /// overwritten with pages of the one it replaced.
    /// </para>
    /// <para>
    /// A pass covers one connection from opening to closing, so work that has to reach one file as a
    /// whole opens its connection once for all of it. A host's delete with its history and a
    /// settings save with the snapshot it updates do that. Left to EF, which opens and closes the
    /// connection around each statement, a restore could swap the file between two of them. A flow
    /// that holds a pass must not open a second connection: while a restore waits for the first
    /// pass, the second waits for the restore, and the restore gives up when its wait runs out.
    /// </para>
    /// <para>
    /// Connections opened outside the factory do not go through the gate: the host summary's own,
    /// which a restore suspends before it shuts the gate; a restore's own, on the file it is about to
    /// put in place; and another process's. For those the restore still relies on SQLite refusing to
    /// take the file out of WAL mode while they hold it.
    /// </para>
    /// </remarks>
    public sealed class DatabaseFileGate
    {
        private static readonly object Held = new();

        private readonly object _lock = new();

        /// <summary>Which connections hold a pass, so each gives back exactly the one it took.</summary>
        private readonly ConditionalWeakTable<DbConnection, object> _passes = new();

        /// <summary>The shut a flow is working inside, whose own connections go through regardless.</summary>
        private readonly AsyncLocal<Shut?> _shutHere = new();

        private int _open;
        private Shut? _shut;

        public DatabaseFileGate()
        {
            Interceptor = new PassInterceptor(this);
        }

        /// <summary>What the context factory adds to its options, so its connections go through here.</summary>
        internal IInterceptor Interceptor { get; }

        /// <summary>Whether a restore has shut the gate, or is waiting for it to empty. For the tests.</summary>
        internal bool IsShut
        {
            get
            {
                lock (_lock)
                {
                    return _shut is not null;
                }
            }
        }

        /// <summary>How many connections hold a pass now. For the tests.</summary>
        internal int OpenConnections
        {
            get
            {
                lock (_lock)
                {
                    return _open;
                }
            }
        }

        /// <summary>
        /// Shuts the gate, runs <paramref name="work"/>, and opens it again however that ends.
        /// Connections that <paramref name="work"/> opens through the factory go through, and every
        /// other connection waits until the gate opens. Throws a <see cref="TimeoutException"/> when
        /// the connections already open hold their passes for longer than
        /// <paramref name="timeout"/>, without running <paramref name="work"/>.
        /// </summary>
        /// <remarks>
        /// New connections wait from the moment the shut is asked for, not only once the gate is
        /// empty: otherwise a page refreshing every few seconds could keep it from ever emptying.
        /// </remarks>
        internal async Task WhileShutAsync(TimeSpan timeout, Func<Task> work, CancellationToken cancellationToken)
        {
            var shut = await ShutAsync(timeout, cancellationToken);
            try
            {
                // Set here rather than in ShutAsync, whose own changes to it would not outlive it.
                _shutHere.Value = shut;
                await work();
            }
            finally
            {
                Open(shut);
            }
        }

        private async Task<Shut> ShutAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Shut shut;
            while (true)
            {
                Task opened;
                lock (_lock)
                {
                    if (_shut is null)
                    {
                        shut = _shut = new Shut();
                        if (_open == 0)
                        {
                            shut.Emptied.TrySetResult();
                        }

                        break;
                    }

                    opened = _shut.Opened.Task;
                }

                await opened.WaitAsync(cancellationToken);
            }

            try
            {
                await shut.Emptied.Task.WaitAsync(timeout, cancellationToken);
                return shut;
            }
            catch
            {
                Open(shut);
                throw;
            }
        }

        private void Open(Shut shut)
        {
            lock (_lock)
            {
                if (_shut == shut)
                {
                    _shut = null;
                }
            }

            shut.Opened.TrySetResult();
        }

        /// <summary>
        /// Gives <paramref name="connection"/> a pass, once the gate is open, or lets it straight
        /// through when the flow opening it is the one that shut the gate.
        /// </summary>
        private async ValueTask EnterAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            while (TryEnter(connection) is { } opened)
            {
                await opened.WaitAsync(cancellationToken);
            }
        }

        /// <summary>
        /// <see cref="EnterAsync"/> for EF's synchronous open, waiting on the gate itself rather than
        /// on a continuation: one posted back to a page's circuit would wait for the very thread
        /// blocked here.
        /// </summary>
        private void Enter(DbConnection connection)
        {
            while (TryEnter(connection) is { } opened)
            {
                opened.Wait();
            }
        }

        /// <summary>
        /// Null when <paramref name="connection"/> may go through now, and otherwise what to wait for
        /// before asking again. A connection already holding a pass keeps that one: EF only tells of
        /// the opens and closes it makes itself, and one closed behind its back is opened again with
        /// the pass it never gave back.
        /// </summary>
        private Task? TryEnter(DbConnection connection)
        {
            lock (_lock)
            {
                if (_shut is null)
                {
                    if (_passes.TryAdd(connection, Held))
                    {
                        _open++;
                    }

                    return null;
                }

                return _shutHere.Value == _shut ? null : _shut.Opened.Task;
            }
        }

        /// <summary>Gives back the pass <paramref name="connection"/> holds, if it holds one.</summary>
        private void Leave(DbConnection connection)
        {
            lock (_lock)
            {
                if (!_passes.Remove(connection))
                {
                    return;
                }

                if (--_open == 0)
                {
                    _shut?.Emptied.TrySetResult();
                }
            }
        }

        /// <summary>A restore's hold on the gate, from asking for it until it opens the gate again.</summary>
        private sealed class Shut
        {
            /// <summary>Set once every pass handed out before the shut has been given back.</summary>
            public TaskCompletionSource Emptied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>Set when the gate opens again, which is what the connections waiting at it wait for.</summary>
            public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Takes a pass as EF opens a connection and gives it back as EF closes or disposes of it,
        /// or when opening it fails. A context disposed with its connection still open, as one that
        /// opened it explicitly can be, says so as a disposal rather than a close.
        /// </summary>
        private sealed class PassInterceptor(DatabaseFileGate gate) : DbConnectionInterceptor
        {
            public override InterceptionResult ConnectionOpening(
                DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            {
                gate.Enter(connection);
                return result;
            }

            public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
                DbConnection connection,
                ConnectionEventData eventData,
                InterceptionResult result,
                CancellationToken cancellationToken = default)
            {
                await gate.EnterAsync(connection, cancellationToken);
                return result;
            }

            public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) =>
                gate.Leave(connection);

            public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
            {
                gate.Leave(connection);
                return Task.CompletedTask;
            }

            public override void ConnectionDisposed(DbConnection connection, ConnectionEndEventData eventData) =>
                gate.Leave(connection);

            public override Task ConnectionDisposedAsync(DbConnection connection, ConnectionEndEventData eventData)
            {
                gate.Leave(connection);
                return Task.CompletedTask;
            }

            public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) =>
                gate.Leave(connection);

            public override Task ConnectionFailedAsync(
                DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
            {
                gate.Leave(connection);
                return Task.CompletedTask;
            }

            public override void ConnectionCanceled(DbConnection connection, ConnectionEndEventData eventData) =>
                gate.Leave(connection);

            public override Task ConnectionCanceledAsync(
                DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                gate.Leave(connection);
                return Task.CompletedTask;
            }
        }
    }
}
