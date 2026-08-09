namespace HostPinger.Core.Options
{
    /// <summary>Settings bound from the "Security" section of the settings overlay.</summary>
    /// <remarks>
    /// A section of its own rather than a key on <see cref="PingerOptions"/>: nothing here reaches
    /// the pinger, and the Configuration page saves that section as a whole.
    /// </remarks>
    public class SecurityOptions
    {
        public const string SectionName = "Security";

        /// <summary>
        /// The password that unlocks the actions changing hosts and settings, in the format
        /// <see cref="Options.PasswordHash"/> writes. Null or empty means no password is set, which
        /// leaves every action unlocked — the state a fresh install is in, and the one it stays in
        /// until someone sets a password.
        /// </summary>
        public string? PasswordHash { get; set; }
    }
}
