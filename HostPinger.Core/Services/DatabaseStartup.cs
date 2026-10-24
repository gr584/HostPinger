using HostPinger.Core.Options;
using Microsoft.Extensions.Hosting;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Opens the database as the host starts: <see cref="HostPingerDatabase.OpenAsync(IServiceProvider, CancellationToken)"/>,
    /// then the sweep of temp files an earlier run left. Done in <see cref="StartingAsync"/>, which
    /// the host runs for every service before it starts any of them, so the web server and the
    /// ping rounds still begin only once the database is migrated and loaded.
    /// </summary>
    /// <remarks>
    /// It used to run in Program before the host started, which on Windows was before the service
    /// had told the service manager it was running. The manager waits about thirty seconds for
    /// that, and an upgrade's migrations over a large history take longer, so it gave up on the
    /// service and an MSI upgrade failed and rolled back. The host's Windows lifetime reports the
    /// service running before any StartingAsync, so the migrations now run inside a service the
    /// manager already counts as started. Under systemd nothing changes: READY=1 still waits for
    /// the whole start, migrations included.
    /// </remarks>
    public sealed class DatabaseStartup(IServiceProvider services, PingerPaths paths) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            // Migrations, then the settings, then the host summary; see OpenAsync for the order.
            await HostPingerDatabase.OpenAsync(services, cancellationToken);

            // A download that died with the process or an upload cut off by a restart leaves its
            // temp file beside the database; nothing holds them now, so they go.
            DatabaseBackup.DeleteLeftoverTempFiles(paths);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
