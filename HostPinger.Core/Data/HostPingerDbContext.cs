using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    public class HostPingerDbContext(DbContextOptions<HostPingerDbContext> options) : DbContext(options)
    {
        public DbSet<MonitoredHost> Hosts => Set<MonitoredHost>();

        public DbSet<PingAttempt> PingAttempts => Set<PingAttempt>();

        public DbSet<ResolverError> ResolverErrors => Set<ResolverError>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MonitoredHost>(host =>
            {
                host.Property(h => h.Name).IsRequired().HasMaxLength(100);
                host.Property(h => h.Address).IsRequired().HasMaxLength(253);
                host.HasIndex(h => h.Address).IsUnique();
                host.HasMany(h => h.PingAttempts)
                    .WithOne(a => a.Host!)
                    .HasForeignKey(a => a.HostId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PingAttempt>(attempt =>
            {
                attempt.HasIndex(a => new { a.HostId, a.TimestampUtc });
                attempt.HasIndex(a => a.TimestampUtc);

                // Unanswered pings only. Locating a host's last downtime means finding its most
                // recent unanswered ping, and the index above cannot seek to it: it would have to
                // walk back over every successful attempt recorded since, which grows without
                // bound for a host that stays up. A partial index holds just the failures, so the
                // seek stays flat no matter how long the host has been healthy.
                attempt.HasIndex(a => new { a.HostId, a.TimestampUtc }, "IX_PingAttempts_Unanswered")
                    .HasFilter("\"RoundtripMs\" IS NULL");
            });

            modelBuilder.Entity<ResolverError>(error =>
            {
                // Matching the column the address is copied from, so a name a host can hold is a
                // name this table can record.
                error.Property(e => e.Address).IsRequired().HasMaxLength(253);

                // Oldest first, which is the order the pruner deletes in.
                error.HasIndex(e => e.TimestampUtc);

                // The page groups every recorded error by address. Leading on Address lets that
                // group run straight off this index in address order — no temporary sort, and no
                // row ever read, since between them the two columns and the row id answer
                // everything the grouping asks for. Only the reason of each address's newest error
                // is then fetched by id, one row per address on the page.
                error.HasIndex(e => new { e.Address, e.TimestampUtc });
            });
        }
    }
}
