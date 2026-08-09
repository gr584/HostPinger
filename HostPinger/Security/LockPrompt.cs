namespace HostPinger.Security
{
    /// <summary>
    /// Lets a page ask for the unlock overlay, which belongs to the layout above it.
    /// </summary>
    /// <remarks>
    /// Scoped, so there is one of these per rendered page: the component asking and the overlay
    /// listening are parts of the same page and share it. It carries nothing but the request —
    /// whether unlocking is even the thing to offer is the overlay's business.
    /// </remarks>
    public sealed class LockPrompt
    {
        public event Action<string?>? Requested;

        /// <summary>
        /// Asks for the overlay.
        /// </summary>
        /// <param name="destination">
        /// Where to go once it is unlocked, for the asker that wants somewhere other than the page
        /// it is asking from — the Security card unlocks in order to reach the password page. Null
        /// stays where it is.
        /// </param>
        public void Request(string? destination = null) => Requested?.Invoke(destination);
    }
}
