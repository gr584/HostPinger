using HostPinger.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Components
{
    /// <summary>
    /// The reads one page makes of the database: each run where it holds up nothing else on the
    /// page, and each dropped when it lands if a read sent after it has landed first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Microsoft.Data.Sqlite does no asynchronous I/O. Its async methods run the statement on the
    /// thread that calls them and hand back a task that has already finished, so a read awaited
    /// inside a component runs on the circuit's dispatcher — the one thread every click, render and
    /// disposal on the page takes its turn on — and holds it for as long as the statement runs. On a
    /// page that reads again every few seconds, every click waits behind whichever read is under
    /// way. A read that outlasts the timer is worse: the next tick is already due when it returns,
    /// nothing in between yields, and the page never gets the thread back. It answers no click from
    /// then on, and closing its tab cannot dispose it either, since disposing is one more thing
    /// waiting for that thread. Against a copy of a production database, with the process held to
    /// two fifths of a core so that the Hosts query took six seconds, the page stopped answering
    /// after its first refresh and went on querying minutes after its tab was closed.
    /// </para>
    /// <para>
    /// So every read after the first goes to the thread pool, and the circuit only comes back to put
    /// what it found on screen. The first stays on the thread that asks for it: until it lands the
    /// page has nothing of its own to draw, and an empty table drawn in the meantime would replace
    /// the prerendered one the reader is already looking at, while the circuit has no handlers
    /// attached yet for a click to be waiting on. Sent off like the rest, it had every page flash
    /// its empty state — "No hosts yet" — on every load.
    /// </para>
    /// <para>
    /// Off the circuit, reads overlap — a refresh can still be out when a click sends one of its
    /// own — and they need not land in the order they were sent. Each is numbered as it is sent and
    /// handed back only if no read sent after it has been handed back already, so the page never
    /// steps back to what the database said before a change the reader has just made. The numbers
    /// are only touched on the circuit, before the read and after it, so they need no lock.
    /// </para>
    /// <para>
    /// A read can also land after the page that sent it has gone: cancelling stops a read at its
    /// next check rather than in the middle of a statement, which SQLite finishes regardless. A
    /// page using this therefore cancels its token source when it goes away and leaves it at that.
    /// Disposing it as well would make the source throw at the code the late read comes back to —
    /// a refresh loop asking it for the token once more — instead of answering that it is
    /// cancelled, and it holds no timer or wait handle that disposing would release.
    /// </para>
    /// <para>
    /// A read that does reach a check after the page has gone ends in an
    /// <see cref="OperationCanceledException"/>, and that is handed back as null, the same as an
    /// overtaken read: there is nothing left to show it on. Leaving a graph is the usual way in.
    /// The change of address supplies the graph's query parameters afresh before the page is
    /// disposed, which sends one last read of every bucket, and it is cancelled partway through.
    /// Thrown on up, Blazor would swallow it one level higher, but only after a debugger watching
    /// for exceptions that user code leaves unhandled had stopped on it, every time. A
    /// cancellation that is not the page's own still comes out as one.
    /// </para>
    /// </remarks>
    public sealed class PageReads
    {
        private int _sent;
        private int _handedBack;

        /// <summary>
        /// Reads through a context of its own, and returns what <paramref name="read"/> found — or
        /// null when there is nothing to show it on: a read sent after this one has been handed back
        /// first, which makes this one older than what the page is already showing, or the page
        /// cancelled it on its way out.
        /// </summary>
        /// <param name="dbFactory">Where the context comes from.</param>
        /// <param name="read">The read itself. Off the circuit it must not touch the page, so
        /// anything of the page's it needs is taken before the call and closed over.</param>
        /// <param name="cancellationToken">The page's own, cancelled when it goes away. It stops the
        /// read at its next check rather than in the middle of a statement, which SQLite finishes
        /// regardless.</param>
        public async Task<T?> ReadAsync<T>(
            IDbContextFactory<HostPingerDbContext> dbFactory,
            Func<HostPingerDbContext, CancellationToken, Task<T>> read,
            CancellationToken cancellationToken)
            where T : class
        {
            var number = ++_sent;
            T found;
            try
            {
                found = _handedBack == 0
                    ? await ReadHereAsync()
                    : await Task.Run(ReadHereAsync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (number < _handedBack)
            {
                return null;
            }

            _handedBack = number;
            return found;

            async Task<T> ReadHereAsync()
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                return await read(db, cancellationToken);
            }
        }
    }
}
