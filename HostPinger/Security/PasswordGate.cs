using System.Security.Claims;
using HostPinger.Core.Options;

namespace HostPinger.Security
{
    /// <summary>
    /// Decides whether a browser may change anything. Everything that adds, edits or deletes a
    /// host, pauses monitoring or saves a setting asks this one question, and the pages render
    /// their controls from the same answer, so what the UI offers and what the server will accept
    /// cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Reads the store on every call rather than capturing its answer, so a password set, changed
    /// or removed on another browser takes effect the moment that save returns.
    /// </remarks>
    public sealed class PasswordGate(UserSettingsStore settings)
    {
        /// <summary>
        /// Names the sign-in as something, since there are no accounts. It is only ever shown to
        /// the browser holding the cookie.
        /// </summary>
        private const string UnlockedName = "Unlocked";

        private const string AuthenticationType = "HostPingerUnlock";

        /// <summary>
        /// Carries <see cref="UserSettingsStore.UnlockStamp"/> as it was when the cookie was
        /// issued: a fingerprint of the password, and of the restore that put the database in
        /// place. Comparing it against the one in force is what makes a change or a removal of the
        /// password, and every restore, invalidate the cookies that came before it.
        /// </summary>
        private const string StampClaimType = "hostpinger:stamp";

        private string? StoredHash => settings.PasswordHash;

        /// <summary>Whether a password stands between a visitor and the actions that change things.</summary>
        public bool IsPasswordSet => !string.IsNullOrEmpty(StoredHash);

        /// <summary>
        /// Whether <paramref name="user"/> may change things: always, while no password is set, and
        /// otherwise only while holding an unlock issued under the password in force now, since the
        /// last restore. A restore puts every browser back to locked, the one that restored
        /// included, even where the backup brings the password already in force: whoever goes on
        /// has to know the password the backup brought.
        /// </summary>
        /// <remarks>
        /// Says nothing of a backup or a restore under way, which keeps every browser from changing
        /// anything whether it is unlocked or not: that is refused where the change is made, at the
        /// <c>ChangeLockout</c>, and ends with no browser's unlock touched.
        /// </remarks>
        public bool IsUnlocked(ClaimsPrincipal? user)
        {
            // Read once: empty is what says there is no password, and the rest is compared.
            var stamp = settings.UnlockStamp;
            if (stamp.Length == 0)
            {
                return true;
            }

            return user?.Identity?.IsAuthenticated == true
                && user.FindFirstValue(StampClaimType) == stamp;
        }

        /// <summary>
        /// Whether <paramref name="password"/> is the one that is set.
        /// </summary>
        /// <remarks>
        /// The plain question, and not the one a page should be asking: guesses arrive through
        /// <see cref="PasswordAttempts"/>, which asks this and counts the asking, so that no form
        /// can offer somebody an unlimited run at the password by forgetting to.
        /// </remarks>
        public bool Verify(string password) => PasswordHash.Verify(StoredHash, password);

        /// <summary>
        /// The principal to sign a browser in as, stamped from the password in force. A save is in
        /// force the moment it returns, so the page that has just written a password can sign its
        /// browser in from here and get the stamp of the hash it wrote.
        /// </summary>
        public ClaimsPrincipal CreatePrincipal()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, UnlockedName),
                    new Claim(StampClaimType, settings.UnlockStamp),
                ],
                AuthenticationType);

            return new ClaimsPrincipal(identity);
        }
    }
}
