using HostPinger.Components;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using HostPinger.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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
            builder.Services.AddDbContextFactory<HostPingerDbContext>(options => options.UseSqlite($"Data Source={paths.DatabasePath}"));

            // Unlocking the actions that change hosts and settings. There are no accounts: signing
            // in means holding the one password, and the cookie says nothing but that. It is
            // deliberately not persistent, so closing the browser locks it again, and SameAsRequest
            // rather than Always because the service is normally reached over plain HTTP — a
            // cookie marked secure there would never come back.
            builder.Services.AddSingleton<PasswordGate>();

            // Every guess goes through here, which is why it is a singleton: what it remembers is
            // how many wrong ones each address has sent lately, and a tally kept per request would
            // be no tally at all.
            builder.Services.AddSingleton<PasswordAttempts>();

            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "hostpinger-unlock";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.LoginPath = "/unlock";
                    options.ReturnUrlParameter = "returnUrl";
                });

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

            // Registered ahead of the monitor so its verdict reaches the log before the first round
            // starts recording hosts as down.
            builder.Services.AddHostedService<IcmpAvailabilityCheck>();
            builder.Services.AddHostedService<PingMonitorService>();

            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HostPingerDbContext>>();
                using var db = dbFactory.CreateDbContext();
                HostPingerDatabase.InitializeAsync(db).GetAwaiter().GetResult();
            }

            // After the migrations, which is what guarantees the table it reads exists.
            app.Services.GetRequiredService<UserSettingsStore>().LoadAsync().GetAwaiter().GetResult();

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

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }

        /// <summary>
        /// What `HostPinger --remove-password` runs instead of the service: the recovery for a
        /// lost password. It writes the same "explicitly no password" row the Password page
        /// writes, so it also cancels a hash configured in appsettings.json or the environment —
        /// which is what somebody who cannot produce the password needs it to do. The running
        /// service reads settings from memory, so this takes effect on its next start.
        /// </summary>
        /// <remarks>
        /// Migrations run first, making the command safe against a database a newer build has not
        /// opened yet — and against no database at all, where it creates one that says no password
        /// rather than failing somebody who is locked out.
        /// </remarks>
        private static void RemovePassword(PingerPaths paths)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={paths.DatabasePath}")
                .Options;
            using var db = new HostPingerDbContext(options);
            HostPingerDatabase.InitializeAsync(db).GetAwaiter().GetResult();
            UserSettingsStore.StageAsync(db, UserSettingsStore.PasswordHashKey, string.Empty)
                .GetAwaiter().GetResult();
            db.SaveChanges();

            Console.WriteLine($"Password removed from {paths.DatabasePath}.");
            Console.WriteLine("Nothing is locked now. Start the service, or restart it if it is running.");
        }
    }
}
