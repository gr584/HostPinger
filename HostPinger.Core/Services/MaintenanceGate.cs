namespace HostPinger.Core.Services
{
    /// <summary>
    /// Makes the ping rounds and the operations that copy or replace the database file take
    /// turns. A round holds it while it writes; a backup snapshot holds it so the copy is of a
    /// settled file; a restore holds it so nothing writes to the file it is about to swap out.
    /// </summary>
    /// <remarks>
    /// A gate rather than stopping the monitor service: stopping a <c>BackgroundService</c> is
    /// one-way, and a restore that failed halfway would leave monitoring off. Holding a lock the
    /// monitor takes per round pauses it for exactly as long as the file work lasts, and releases
    /// it however that work ends.
    /// </remarks>
    public sealed class MaintenanceGate
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Waits for the gate and holds it until the returned handle is disposed.</summary>
        public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return new Releaser(_gate);
        }

        private sealed class Releaser(SemaphoreSlim gate) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    gate.Release();
                }
            }
        }
    }
}
