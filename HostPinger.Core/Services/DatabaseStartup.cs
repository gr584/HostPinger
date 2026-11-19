using HostPinger.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Opens the database as the host starts: <see cref="HostPingerDatabase.OpenAsync(IServiceProvider, CancellationToken)"/>,
    /// then the sweep of temp files an earlier run left, then a real size limit in place of a
    /// zero, which used to turn pruning off (<see cref="DatabaseSizeLimit.SettleAsync"/>). Done in
    /// <see cref="StartingAsync"/>, which the host runs for every service before it starts any of
    /// them, so the web server and the ping rounds still begin only once the database is migrated
    /// and loaded.
    /// </summary>
    /// <remarks>
    /// Under systemd, READY=1 waits for the whole start, migrations included. An upgrade's
    /// migrations over a large history can take minutes, which is why the packaged unit sets
    /// TimeoutStartSec=infinity.
    /// </remarks>
    public sealed class DatabaseStartup(IServiceProvider services, PingerPaths paths) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            // Migrations, then the settings, then the host summary; see OpenAsync for the order.
            await HostPingerDatabase.OpenAsync(services, cancellationToken);

            // A download that died with the process or an upload cut off by a restart leaves its
            // temp file beside the database, with whatever SQLite was keeping beside that; nothing
            // holds them now, so they go.
            DatabaseBackup.DeleteLeftoverTempFiles(paths);

            // Before the first round, which would otherwise go on not pruning, and after the sweep,
            // so that what the leftovers took counts as the room it is.
            await services.GetRequiredService<DatabaseSizeLimit>().SettleAsync(cancellationToken);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
