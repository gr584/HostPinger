namespace HostPinger.Security
{
    /// <summary>
    /// The names the unlock and lock forms post under.
    /// </summary>
    /// <remarks>
    /// The overlay that carries those forms lives in the layout and is interactive, and a form
    /// posted from an interactive component is an ordinary browser post rather than anything
    /// Blazor handles — so it has to land on a statically rendered component, which is what the
    /// Unlock and Lock pages are. The two ends are different components and only line up because
    /// these say so, which is why they are here rather than spelled out at either end.
    /// </remarks>
    internal static class LockFormNames
    {
        /// <summary>Chooses which form on the receiving page handles the post.</summary>
        public const string Handler = "_handler";

        public const string UnlockHandler = "unlock";

        public const string LockHandler = "lock";

        /// <summary>The password itself.</summary>
        public const string Password = "password";

        /// <summary>
        /// Where to go once it works, carried on the query string of the post. Usually the page the
        /// overlay was opened on, but not always: unlocking in order to change the password asks to
        /// be taken to the password page rather than back to where the asking happened.
        /// </summary>
        public const string ReturnUrl = "returnUrl";

        /// <summary>
        /// The page the overlay was opened on. Its presence is what says the post came from the
        /// overlay, and its value is where a wrong password goes back to — which is that page
        /// rather than <see cref="ReturnUrl"/>, because the overlay has to reopen where it was.
        /// </summary>
        public const string FromOverlay = "overlay";

        /// <summary>Marks that return trip, and is what reopens the overlay with its error.</summary>
        public const string FailedQuery = "unlockFailed";

        /// <summary>
        /// Asks for the overlay on arrival, for a page that cannot ask for it directly. The pages
        /// that write cookies are rendered statically and so have no click to run anything on; a
        /// link carrying this is how they offer to unlock.
        /// </summary>
        public const string OpenQuery = "unlock";

        /// <summary>
        /// Carries the destination across that return trip, so that a password typed wrongly the
        /// first time still arrives where it was going.
        /// </summary>
        public const string NextQuery = "unlockNext";
    }
}
