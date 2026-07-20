using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HostPinger.Core.Data
{
    /// <summary>Lets `dotnet ef` create the context without the web app's configuration.</summary>
    public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HostPingerDbContext>
    {
        public HostPingerDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite("Data Source=hostpinger-design.db")
                .Options;
            return new HostPingerDbContext(options);
        }
    }
}
