using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HostPinger.Security
{
    /// <summary>
    /// Single-use tokens that let a backup download endpoint know the request came from an
    /// unlocked page a moment ago. The page is a circuit and the download is a plain GET, so the
    /// unlock decision has to travel between them somehow — this is that, and nothing more: the
    /// endpoint still asks <see cref="PasswordGate"/> the real question.
    /// </summary>
    public sealed class BackupDownloadTokens(TimeProvider? timeProvider = null)
    {
        /// <summary>
        /// Long enough for the browser to turn the navigation into a request, short enough that a
        /// token in a pasted URL or a proxy log is stale by the time anyone reads it there.
        /// </summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new();
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        /// <summary>Mints a token the endpoint will accept once within the next two minutes.</summary>
        public string Issue()
        {
            // Issuing is also when the table is swept: a token nobody spent would otherwise sit
            // in the dictionary until the process ends.
            var now = _timeProvider.GetUtcNow();
            foreach (var (token, expiry) in _expiries)
            {
                if (expiry <= now)
                {
                    _expiries.TryRemove(token, out _);
                }
            }

            var issued = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _expiries[issued] = now + Lifetime;
            return issued;
        }

        /// <summary>
        /// Whether <paramref name="token"/> was issued and is still fresh, spending it either way:
        /// a token that has been seen is done, even if what saw it was a refusal.
        /// </summary>
        public bool TryConsume(string token) =>
            _expiries.TryRemove(token, out var expiry) && expiry > _timeProvider.GetUtcNow();
    }
}
