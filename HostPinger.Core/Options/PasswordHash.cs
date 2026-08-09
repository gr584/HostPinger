using System.Security.Cryptography;
using System.Text;

namespace HostPinger.Core.Options
{
    /// <summary>
    /// The stored form of the web UI password: PBKDF2 over a random salt, written as one string
    /// that carries everything <see cref="Verify"/> needs to check a candidate against it.
    /// </summary>
    /// <remarks>
    /// Hand-rolled over <see cref="Rfc2898DeriveBytes"/> rather than taken from ASP.NET Core
    /// Identity, whose hasher would work but lives in the web framework: this is the one thing the
    /// settings overlay stores that has to be understood by both the library that writes the file
    /// and the tests that read it back, and neither references the framework.
    /// </remarks>
    public static class PasswordHash
    {
        /// <summary>
        /// Marks both the algorithm and the layout of everything after it, so a future change can
        /// be recognised rather than mistaken for a corrupt value.
        /// </summary>
        private const string Version = "v1";

        // OWASP's floor for PBKDF2-HMAC-SHA256 at the time of writing, and about a tenth of a
        // second per attempt on ordinary hardware. That cost is what a run of guesses meets first,
        // and it is what holds if the rest ever fails: the web UI turns an address away for longer
        // and longer as it goes on guessing, but nothing of that reaches down here, and a copy of
        // the settings file taken off the machine leaves this as the only thing in the way.
        private const int Iterations = 210_000;

        private const int SaltBytes = 16;
        private const int SubkeyBytes = 32;

        private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

        /// <summary>Hashes <paramref name="password"/> under a freshly generated salt.</summary>
        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, SubkeyBytes);
            return $"{Version}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(subkey)}";
        }

        /// <summary>
        /// Whether <paramref name="password"/> is the one <paramref name="stored"/> was made from.
        /// </summary>
        /// <remarks>
        /// Anything unreadable answers false rather than throwing. The overlay is a plain JSON file
        /// meant to be editable by hand — a truncated or half-deleted value must leave the
        /// application locked and working, not failing on every request.
        /// </remarks>
        public static bool Verify(string? stored, string password)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return false;
            }

            var parts = stored.Split('.');
            if (parts.Length != 4
                || parts[0] != Version
                || !int.TryParse(parts[1], out var iterations)
                || iterations <= 0)
            {
                return false;
            }

            byte[] salt;
            byte[] subkey;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                subkey = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }

            if (salt.Length == 0 || subkey.Length == 0)
            {
                return false;
            }

            // The stored iteration count is used rather than the constant above, so a value written
            // by an older build still verifies after the cost is raised.
            var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, subkey.Length);
            return CryptographicOperations.FixedTimeEquals(candidate, subkey);
        }

        /// <summary>
        /// A short fingerprint of <paramref name="stored"/>, carried by the unlock cookie so that
        /// changing or removing the password stops the cookies issued under the old one from
        /// unlocking anything. Empty when no password is stored.
        /// </summary>
        /// <remarks>
        /// A digest rather than the hash itself: the cookie is encrypted, but there is no reason to
        /// put the stored value into something the browser holds when a fingerprint of it answers
        /// the only question being asked.
        /// </remarks>
        public static string Stamp(string? stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return string.Empty;
            }

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(stored));
            return Convert.ToBase64String(digest, 0, 12);
        }
    }
}
