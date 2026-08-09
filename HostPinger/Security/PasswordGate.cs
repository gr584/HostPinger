using System.Security.Claims;
using HostPinger.Core.Options;
using Microsoft.Extensions.Options;

namespace HostPinger.Security
{
    /// <summary>
    /// Decides whether a browser may change anything. Everything that adds, edits or deletes a
    /// host, pauses monitoring or saves a setting asks this one question, and the pages render
    /// their controls from the same answer, so what the UI offers and what the server will accept
    /// cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every call rather than
    /// capturing it, so a password set, changed or removed on another browser — or by hand in the
    /// overlay file — takes effect as soon as the configuration reload sees it.
    /// </remarks>
    public sealed class PasswordGate(IOptionsMonitor<SecurityOptions> options)
    {
        /// <summary>
        /// Names the sign-in as something, since there are no accounts. It is only ever shown to
        /// the browser holding the cookie.
        /// </summary>
        private const string UnlockedName = "Unlocked";

        private const string AuthenticationType = "HostPingerUnlock";

        /// <summary>
        /// Carries <see cref="PasswordHash.Stamp"/> of the password the cookie was issued under.
        /// Comparing it against the stored password is what makes a change or a removal invalidate
        /// the cookies that came before it.
        /// </summary>
        private const string StampClaimType = "hostpinger:stamp";

        /// <summary>
        /// How long <see cref="WaitForSaveAsync"/> gives the configuration reload before going on
        /// without it. The reload lands in a fraction of a second; this is only a ceiling so that a
        /// change token that never fires cannot hold a page open.
        /// </summary>
        private static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(2);

        private string? StoredHash => options.CurrentValue.PasswordHash;

        /// <summary>Whether a password stands between a visitor and the actions that change things.</summary>
        public bool IsPasswordSet => !string.IsNullOrEmpty(StoredHash);

        /// <summary>
        /// Whether <paramref name="user"/> may change things: always, while no password is set, and
        /// otherwise only while holding an unlock issued under the password in force now.
        /// </summary>
        public bool IsUnlocked(ClaimsPrincipal? user)
        {
            var stored = StoredHash;
            if (string.IsNullOrEmpty(stored))
            {
                return true;
            }

            return user?.Identity?.IsAuthenticated == true
                && user.FindFirstValue(StampClaimType) == PasswordHash.Stamp(stored);
        }

        /// <summary>Whether <paramref name="password"/> is the one that is set.</summary>
        public bool Verify(string password) => PasswordHash.Verify(StoredHash, password);

        /// <summary>
        /// Waits until a hash just written to the overlay is the one this gate reports, or until
        /// <paramref name="timeout"/> passes.
        /// </summary>
        /// <remarks>
        /// Saving writes a file, and the file has to be noticed and reloaded before
        /// <c>IOptionsMonitor</c> answers with it — around a quarter of a second. A page that saves
        /// and redirects beats that easily, and the page it lands on then draws the state that was
        /// just replaced: removing the password lands on a Configuration page still saying one is
        /// needed and offering nothing but disabled boxes, which is the opposite of what just
        /// happened. Waiting here is what makes the next page true.
        /// <para>
        /// A timeout gives up rather than throwing. The pages that follow refresh themselves on a
        /// timer, so the worst a missed reload costs is the stale render it was already costing.
        /// </para>
        /// </remarks>
        public async Task WaitForSaveAsync(
            string? hash,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            // Removing writes the key as empty rather than deleting it, so that is what "no
            // password" looks like coming back through the configuration.
            var expected = hash ?? string.Empty;
            if (HasLanded(expected))
            {
                return;
            }

            var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = options.OnChange(_ =>
            {
                if (HasLanded(expected))
                {
                    landed.TrySetResult();
                }
            });

            // Again, now that the callback is in place: the reload can arrive in the moment between
            // the check above and the subscription, and nothing would tell us afterwards.
            if (HasLanded(expected))
            {
                return;
            }

            try
            {
                await landed.Task.WaitAsync(timeout ?? ReloadTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
        }

        private bool HasLanded(string expected) => (StoredHash ?? string.Empty) == expected;

        /// <summary>
        /// The principal to sign a browser in as.
        /// </summary>
        /// <param name="hash">
        /// The password to stamp the unlock with, for the caller that has just saved one:
        /// the configuration reload is asynchronous, so a principal built from the stored value
        /// immediately after a save would carry the stamp of the password that was replaced and
        /// lock that browser out the moment the reload caught up. Null takes the stored value,
        /// which is what unlocking against an unchanged password wants.
        /// </param>
        public ClaimsPrincipal CreatePrincipal(string? hash = null)
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, UnlockedName),
                    new Claim(StampClaimType, PasswordHash.Stamp(hash ?? StoredHash)),
                ],
                AuthenticationType);

            return new ClaimsPrincipal(identity);
        }
    }
}
