using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace HostPinger.Security
{
    /// <summary>
    /// Single-use tokens that hand a snapshot an unlocked page has just written, having been given
    /// the current password if one is set, to the backup download endpoint. The page is a circuit
    /// and the download is a plain GET, so that decision has to travel between them somehow — this
    /// is that, and nothing more: the endpoint still asks <see cref="PasswordGate"/> whether the
    /// browser is unlocked.
    /// </summary>
    /// <remarks>
    /// A token holds its snapshot until it is spent. One nobody spends in time is deleted along
    /// with it, so a download the browser never asked for does not leave a copy of the database
    /// on the disk until the next start sweeps it away.
    /// </remarks>
    public sealed class BackupDownloadTokens(TimeProvider? timeProvider = null)
    {
        /// <summary>
        /// Long enough for the browser to turn the navigation into a request, short enough that a
        /// token in a pasted URL or a proxy log is stale by the time anyone reads it there.
        /// </summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, Issued> _issued = new();
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// Mints a token the endpoint will accept once within the next two minutes, for the
        /// snapshot at <paramref name="snapshotPath"/>.
        /// </summary>
        public string Issue(string snapshotPath)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expiry = _timeProvider.CreateTimer(Expire, token, Lifetime, Timeout.InfiniteTimeSpan);
            _issued[token] = new Issued(snapshotPath, _timeProvider.GetUtcNow() + Lifetime, expiry);
            return token;
        }

        /// <summary>
        /// Whether <paramref name="token"/> was issued and is still fresh, and if so the snapshot
        /// it was issued for, which is the caller's from then on. The token is spent either way: a
        /// token that has been seen is done, even if what saw it was a refusal.
        /// </summary>
        public bool TryConsume(string token, [NotNullWhen(true)] out string? snapshotPath)
        {
            snapshotPath = null;
            if (!_issued.TryRemove(token, out var issued))
            {
                return false;
            }

            issued.Expiry.Dispose();

            // The timer runs a moment late, if anything, so a token can be asked for after its time
            // and before the timer has spent it.
            if (issued.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                Delete(issued.SnapshotPath);
                return false;
            }

            snapshotPath = issued.SnapshotPath;
            return true;
        }

        private void Expire(object? state)
        {
            if (_issued.TryRemove((string)state!, out var issued))
            {
                issued.Expiry.Dispose();
                Delete(issued.SnapshotPath);
            }
        }

        private static void Delete(string snapshotPath)
        {
            try
            {
                File.Delete(snapshotPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the sweep at the next start. An exception out of a timer's callback
                // would end the process.
            }
        }

        private sealed record Issued(string SnapshotPath, DateTimeOffset ExpiresAt, ITimer Expiry);
    }
}
