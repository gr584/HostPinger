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

            builder.Services.Configure<PingerOptions>(builder.Configuration.GetSection(PingerOptions.SectionName));

            var databasePath = PingerOptions.ResolveDatabasePath(
                builder.Configuration[$"{PingerOptions.SectionName}:{nameof(PingerOptions.DatabasePath)}"],
                builder.Environment.ContentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            builder.Services.AddDbContextFactory<HostPingerDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

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
