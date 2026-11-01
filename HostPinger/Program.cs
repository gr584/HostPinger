using HostPinger.Components;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using HostPinger.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace HostPinger
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // A break-glass command rather than configuration, so it is pulled out before the
            // command-line configuration provider can trip over a switch with no value.
            var removePassword = args.Contains("--remove-password");
            if (removePassword)
            {
                args = args.Where(a => a != "--remove-password").ToArray();
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                // A Windows service starts with System32 as the working directory, and a systemd
                // unit inherits whatever the manager had; anchor the content root next to the
                // executable instead so appsettings.json is found either way.
                ContentRootPath = WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService()
                    ? AppContext.BaseDirectory
                    : null,
            });

            // Each is a no-op unless the process is actually running under that service manager,
            // so registering both leaves a console run untouched.
            builder.Services.AddWindowsService(options => options.ServiceName = "HostPinger");
            builder.Services.AddSystemd();

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // Where a page's prerender leaves what it read for the live page that follows, so that
            // opening the page reads once. One for the process, since that is what the prerender
            // and the circuit have in common.
            builder.Services.AddSingleton<PrerenderHandoff>();

            // The database path has to come from the file and environment configuration alone: the
            // settings stored inside the database cannot be the place that says where it is.
            var paths = PingerPaths.Resolve(
                builder.Configuration[$"{PingerOptions.SectionName}:{nameof(PingerOptions.DatabasePath)}"],
                builder.Environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

            if (removePassword)
            {
                RemovePassword(paths);
                return;
            }

            // These bindings are the defaults the store below falls back to for any setting the
            // UserSettings table holds no row for.
            builder.Services.Configure<PingerOptions>(builder.Configuration.GetSection(PingerOptions.SectionName));
            builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
            builder.Services.AddSingleton(paths);

            // Settings edited on the Configuration page live in the UserSettings table, read
            // through this store: a save is in force the moment it returns.
            builder.Services.AddSingleton<UserSettingsStore>();
            builder.Services.AddHostPingerDatabase(paths);

            // Unlocking the actions that change hosts and settings; see UnlockCookie for the cookie
            // an unlock is made of.
            builder.Services.AddSingleton<PasswordGate>();

            // Every guess goes through here, which is why it is a singleton: what it remembers is
            // how many wrong ones each address has sent lately, and a tally kept per request would
            // be no tally at all.
            builder.Services.AddSingleton<PasswordAttempts>();

            // The address a guess on a circuit comes from, which has no request of its own to ask:
            // the Configuration page asks for the current password before a backup or a restore.
            builder.Services.AddHttpContextAccessor();

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(UnlockCookie.Configure);

            // Puts the signed-in principal where the components can see it, which is what the pages
            // pass to PasswordGate.
            builder.Services.AddCascadingAuthenticationState();

            // How a page asks the layout for the unlock overlay. Scoped: one per rendered page,
            // shared by the component asking and the overlay listening.
            builder.Services.AddScoped<LockPrompt>();

            // Off Windows the key ring defaults to the user profile, which a container throws away
            // on every restart; the Blazor circuits of any open browser then fail to reconnect, and
            // the unlock cookie, which the same keys protect, stops being readable. Windows keeps
            // its DPAPI-backed default, which the installed service already persists.
            if (!OperatingSystem.IsWindows())
            {
                builder.Services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(
                        Path.Combine(Path.GetDirectoryName(paths.DatabasePath)!, "DataProtection-Keys")));
            }

            builder.Services.AddSingleton<IPingSender, PingSender>();
            builder.Services.AddSingleton<DatabasePruner>();

            // Backup and restore. The gate is what the monitor, a snapshot and a restore all take
            // turns on, so it must be the one instance they share.
            builder.Services.AddSingleton<MaintenanceGate>();
            builder.Services.AddSingleton<DatabaseBackup>();
            builder.Services.AddSingleton<BackupDownloadTokens>();

            // What the size limit may be on the disk the database is on, which a backup's copy
            // needs half of.
            builder.Services.AddSingleton<DatabaseSizeLimit>();

            // Migrations, then the settings, then the host summary, then the temp-file sweep, before
            // the web server or any ping round starts; see DatabaseStartup for why it is a hosted
            // service rather than a step ahead of the host.
            builder.Services.AddHostedService<DatabaseStartup>();

            // Registered ahead of the monitor so its verdict reaches the log before the first round
            // starts recording hosts as down.
            builder.Services.AddHostedService<IcmpAvailabilityCheck>();
            builder.Services.AddHostedService<PingMonitorService>();

            // What the Hosts page draws, read once per change by a loop of its own rather than by
            // every page showing it. One instance, both the service pages ask and the loop that
            // keeps it current.
            builder.Services.AddSingleton<HostSummaryCache>();
            builder.Services.AddHostedService(services => services.GetRequiredService<HostSummaryCache>());

            // Adding, editing, pausing and deleting hosts, each handing back the summary read after
            // it, so the page that made a change draws it next.
            builder.Services.AddSingleton<HostStore>();

            // The reads the other pages make when they are asked for, each through a context of its
            // own, so that no page holds one.
            builder.Services.AddSingleton<DatabaseReads>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

            // Ahead of the antiforgery middleware, so the unlock and password forms are validated
            // against the browser they were served to and the pages below know who is asking.
            app.UseAuthentication();
            app.UseAntiforgery();

            // Liveness probe for container orchestration. It deliberately touches nothing: a
            // pruning pass holding the database busy is not a reason to restart the container.
            app.MapGet("/health", () => Results.Ok("Healthy"));

            // The backup download. A plain GET rather than anything on the circuit, because a
            // gigabyte belongs on the HTTP pipe, not the SignalR one. The page writes the snapshot
            // first, where it can show how far the copy has got, and the single-use token hands it
            // over: it proves the request came from an unlocked page moments ago, given the current
            // password if one is set, and the cookie check keeps the endpoint exactly as locked as
            // the button that leads here. NotFound either way, so a probe cannot tell a bad token
            // from there being anything to find.
            app.MapGet("/database-backup", (
                string token,
                HttpContext context,
                BackupDownloadTokens tokens,
                PasswordGate gate) =>
            {
                if (!tokens.TryConsume(token, out var snapshotPath))
                {
                    return Results.NotFound();
                }

                // Opened before the cookie is looked at, because DeleteOnClose ties the snapshot's
                // life to the stream: however the request ends — the download completed or
                // cancelled, the browser gone, or a refusal just below — closing it removes the file.
                var snapshot = new FileStream(
                    snapshotPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 81_920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                if (!gate.IsUnlocked(context.User))
                {
                    snapshot.Dispose();
                    return Results.NotFound();
                }

                return Results.File(
                    snapshot,
                    "application/octet-stream",
                    $"hostpinger-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            });

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }

        /// <summary>
        /// What `HostPinger --remove-password` runs instead of the service; see
        /// <see cref="UserSettingsStore.RemovePasswordAsync"/> for what it writes and why.
        /// </summary>
        private static void RemovePassword(PingerPaths paths)
        {
            var clearedLock = UserSettingsStore.RemovePasswordAsync(paths.DatabasePath).GetAwaiter().GetResult();

            if (clearedLock)
            {
                Console.WriteLine("Cleared a lock that an interrupted migration had left, then migrated the database.");
            }

            Console.WriteLine($"Password removed from {paths.DatabasePath}.");
            Console.WriteLine("Nothing is locked now. Start the service, or restart it if it is running.");
        }
    }
}
