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

            // Both paths are read straight from appsettings.json, before the overlay below joins the
            // configuration: the overlay cannot be the place that says where the overlay is.
            var paths = PingerPaths.Resolve(
                builder.Configuration[$"{PingerOptions.SectionName}:{nameof(PingerOptions.DatabasePath)}"],
                builder.Configuration[$"{PingerOptions.SectionName}:{nameof(PingerOptions.UserSettingsPath)}"],
                builder.Environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

            // Settings edited on the Configuration page live in an overlay layered on top of
            // appsettings.json. Registering it last makes it win, and reloadOnChange lets a save
            // reach IOptionsMonitor without restarting the service.
            builder.Configuration.AddJsonFile(paths.SettingsPath, optional: true, reloadOnChange: true);

            builder.Services.Configure<PingerOptions>(builder.Configuration.GetSection(PingerOptions.SectionName));
            builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<PingerSettingsStore>();
            builder.Services.AddDbContextFactory<HostPingerDbContext>(options => options.UseSqlite($"Data Source={paths.DatabasePath}"));

            // Unlocking the actions that change hosts and settings. There are no accounts: signing
            // in means holding the one password, and the cookie says nothing but that. It is
            // deliberately not persistent, so closing the browser locks it again, and SameAsRequest
            // rather than Always because the service is normally reached over plain HTTP — a
            // cookie marked secure there would never come back.
            builder.Services.AddSingleton<PasswordGate>();
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
    }
}
