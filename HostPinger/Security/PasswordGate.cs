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
        /// Carries <see cref="PasswordHash.Stamp"/> of the password the cookie was issued under.
        /// Comparing it against the stored password is what makes a change or a removal invalidate
        /// the cookies that came before it.
        /// </summary>
        private const string StampClaimType = "hostpinger:stamp";

        private string? StoredHash => settings.PasswordHash;

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
                    new Claim(StampClaimType, PasswordHash.Stamp(StoredHash)),
                ],
                AuthenticationType);

            return new ClaimsPrincipal(identity);
        }
    }
}
