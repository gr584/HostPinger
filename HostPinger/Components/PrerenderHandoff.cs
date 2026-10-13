using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HostPinger.Components
{
    /// <summary>
    /// What a page read while it was being prerendered, held for a moment so that the live page
    /// which follows can start from it instead of reading the same thing again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The prerender and the circuit are two renders of one page in this one process, a fraction
    /// of a second apart, and each used to read for itself: opening the graph on a two-month
    /// outage read its whole range twice before the page answered a click. Blazor's own way across
    /// is persistent component state, which writes the value into the page and has the browser
    /// send it back as the circuit starts. For a graph that is the whole series both ways, and one
    /// host's two-month graph came to 61 KB, past the 32 KB SignalR accepts from a browser by
    /// default — the circuit was refused and the page never came alive. Raising that limit would
    /// widen what any browser may send for the sake of something that never needed to leave the
    /// server.
    /// </para>
    /// <para>
    /// So the read stays here, and only the id it is held under travels through the page, as
    /// persistent component state. It is held for thirty seconds and taken once, by whichever
    /// circuit presents the id; a page nobody goes on to open lets its read expire. Every hold
    /// costs a read of the database first, which already bounds how fast they can arrive, but the
    /// number held at once is capped as well, so that requests for pages no browser connects to
    /// cannot pile reads up here for half a minute each.
    /// </para>
    /// </remarks>
    public sealed class PrerenderHandoff(TimeProvider? timeProvider = null)
    {
        /// <summary>
        /// Long enough for a browser to load the prerendered page and start its circuit, short
        /// enough that a read nobody takes is soon let go of.
        /// </summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

        /// <summary>How many reads may be held at once; past it, pages read for themselves again.</summary>
        private const int Capacity = 64;

        private readonly ConcurrentDictionary<string, (object Value, DateTimeOffset Expiry)> _held = new();
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// Holds <paramref name="value"/> for the next thirty seconds and returns the id to take it
        /// by, or returns null when as many reads are held as may be.
        /// </summary>
        public string? Hold(object value)
        {
            // Holding is also when the table is swept: a read nobody took would otherwise stay
            // until the process ends.
            var now = _timeProvider.GetUtcNow();
            foreach (var (id, held) in _held)
            {
                if (held.Expiry <= now)
                {
                    _held.TryRemove(id, out _);
                }
            }

            if (_held.Count >= Capacity)
            {
                return null;
            }

            var issued = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _held[issued] = (value, now + Lifetime);
            return issued;
        }

        /// <summary>
        /// What was held under <paramref name="id"/>, while it is still fresh and is a
        /// <typeparamref name="T"/>, letting go of it either way: it was one page's, and that page
        /// has had its chance at it now.
        /// </summary>
        public T? Take<T>(string id)
            where T : class =>
            _held.TryRemove(id, out var held) && held.Expiry > _timeProvider.GetUtcNow()
                ? held.Value as T
                : null;
    }
}
