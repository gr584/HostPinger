using Microsoft.EntityFrameworkCore;

namespace HostPinger.Core.Data
{
    public class HostPingerDbContext(DbContextOptions<HostPingerDbContext> options) : DbContext(options)
    {
        public DbSet<MonitoredHost> Hosts => Set<MonitoredHost>();

        public DbSet<PingAttempt> PingAttempts => Set<PingAttempt>();

        public DbSet<ResolverError> ResolverErrors => Set<ResolverError>();

        public DbSet<UserSetting> UserSettings => Set<UserSetting>();

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

                // Unanswered pings only. Locating a host's last downtime means finding its most
                // recent unanswered ping, and the index above cannot seek to it: it would have to
                // walk back over every successful attempt recorded since, which grows without
                // bound for a host that stays up. A partial index holds just the failures, so the
                // seek stays flat no matter how long the host has been healthy.
                attempt.HasIndex(a => new { a.HostId, a.TimestampUtc }, "IX_PingAttempts_Unanswered")
                    .HasFilter("\"RoundtripMs\" IS NULL");

                // The other half of that pair, and the same argument the other way round. A host's
                // status and the start of its last downtime are both the last ping it answered
                // before a moment, and the composite index cannot seek to that one either: it walks
                // back over every ping missed since, which is the whole of an outage. That walk is
                // unbounded for a host that is down and it is paid on every five-second refresh of
                // the Hosts page — a host unanswered for two million pings cost twelve seconds of
                // it. Answered pings are the common case rather than the rare one, so this index
                // costs what the one above saves; the pair is still the cheapest way to have both
                // ends of an outage be a seek.
                attempt.HasIndex(a => new { a.HostId, a.TimestampUtc }, "IX_PingAttempts_Answered")
                    .HasFilter("\"RoundtripMs\" IS NOT NULL");
            });

            modelBuilder.Entity<UserSetting>(setting =>
            {
                // Long enough for any configuration path this application will ever write, and a
                // bound at all so a stray write cannot make a key the size of a document.
                setting.HasKey(s => s.Key);
                setting.Property(s => s.Key).HasMaxLength(100);
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
