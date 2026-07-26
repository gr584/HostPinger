using HostPinger.Components;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.EntityFrameworkCore;
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
                // A Windows service starts with System32 as the working directory; anchor the
                // content root next to the executable instead.
                ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
            });

            builder.Services.AddWindowsService(options => options.ServiceName = "HostPinger");

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            var paths = PingerPaths.Resolve(
                builder.Configuration[$"{PingerOptions.SectionName}:{nameof(PingerOptions.DatabasePath)}"],
                builder.Environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

            // Settings edited on the Configuration page live in an overlay layered on top of
            // appsettings.json. Registering it last makes it win, and reloadOnChange lets a save
            // reach IOptionsMonitor without restarting the service.
            builder.Configuration.AddJsonFile(paths.SettingsPath, optional: true, reloadOnChange: true);

            builder.Services.Configure<PingerOptions>(builder.Configuration.GetSection(PingerOptions.SectionName));
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<PingerSettingsStore>();
            builder.Services.AddDbContextFactory<HostPingerDbContext>(options => options.UseSqlite($"Data Source={paths.DatabasePath}"));

            builder.Services.AddSingleton<IPingSender, PingSender>();
            builder.Services.AddSingleton<DatabasePruner>();
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
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
