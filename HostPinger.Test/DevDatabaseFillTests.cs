using System.Diagnostics;
using System.Globalization;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HostPinger.Test
{
    /// <summary>
    /// Fills the development database with enough recorded history to measure the application
    /// against, rather than to assert anything about it. It is a tool wearing a test's clothes:
    /// NUnit is here because it is already wired up to the projects, the settings and the schema,
    /// and because "dotnet test" is a shorter road to a seeded database than a second executable
    /// would be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The history it writes is one host pinged at the configured interval, up for the first
    /// quarter of the period, unanswered for the middle half of it, and up again for the last
    /// quarter — so the outage a page has to find sits as far from both ends of the table as it
    /// can, and half of every index is on the wrong side of it.
    /// </para>
    /// <para>
    /// Explicit, and deliberately so: it takes over the localhost host — deleting any other host
    /// answering to that name or address, along with everything recorded against them — and then
    /// writes hundreds of megabytes over the top, which is not something a suite run should ever
    /// do on its way past. Run it by name:
    /// <code>dotnet test --filter FullyQualifiedName~DevDatabaseFillTests</code>
    /// </para>
    /// </remarks>
    [Explicit("Rewrites the development database. Run it deliberately, never as part of the suite.")]
    [Category("DevData")]
    [NonParallelizable]
    public class DevDatabaseFillTests
    {
        /// <summary>
        /// The host this fixture owns. Either half is enough to claim a row — see
        /// <see cref="EnsureSeedHostAsync"/> — so the pair is what a claimed row is rewritten to
        /// rather than only what a new one is created with.
        /// </summary>
        private const string SeedHostName = "localhost";

        /// <inheritdoc cref="SeedHostName"/>
        private const string SeedHostAddress = "127.0.0.1";

        /// <summary>How much of the configured maximum the database should end up at.</summary>
        private const double FillFraction = 0.5;

        /// <summary>How much of the fake period the host spends unanswered, in one unbroken run.</summary>
        private const double DownFraction = 0.5;

        /// <summary>
        /// What an answered ping records. Zero is not a round trip any real network produces, but
        /// it is what was asked for, and it costs nothing to store: SQLite spends no payload bytes
        /// on an integer zero, which is exactly what it spends on the nulls of the outage. Every
        /// row is therefore the same size whichever side of the outage it falls on.
        /// </summary>
        private const int UpRoundtripMs = 0;

        /// <summary>
        /// Rows per transaction. Large enough that the commits are not what the fill spends its
        /// time on, small enough that the rollback journal of any one of them stays modest.
        /// </summary>
        private const int InsertBatchRows = 250_000;

        /// <summary>
        /// How many rows the calibration writes. Enough that the fixed cost of an empty schema
        /// disappears into the average, and that the b-tree has settled into the fill it keeps for
        /// the rest of the run.
        /// </summary>
        private const int CalibrationRows = 200_000;

        /// <summary>
        /// The format EF Core's SQLite provider stores a <see cref="DateTime"/> in. These rows go
        /// in through ADO.NET rather than through EF, so this has to match it exactly: the column
        /// is text, every query orders and ranges over it as text, and a row written to a
        /// different width would sort into the wrong place. The upper-case F is the part that is
        /// easy to get wrong — trailing zeros are dropped, so a whole second is stored with no
        /// fractional part at all.
        /// </summary>
        private const string SqliteDateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        [Test]
        public async Task FillDevelopmentDatabaseWithFakeHistory()
        {
            var contentRoot = FindApplicationContentRoot();
            var configuration = new ConfigurationBuilder()
                .SetBasePath(contentRoot)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Development.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            var paths = PingerPaths.Resolve(
                configuration[$"{PingerOptions.SectionName}:{nameof(PingerOptions.DatabasePath)}"],
                contentRoot);

            GuardAgainstTheInstalledDatabase(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
            Report($"Database   {paths.DatabasePath}");

            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={paths.DatabasePath}")
                .Options;

            // The same two steps the application takes at startup, in the same order, so a
            // database that does not exist yet is created exactly as it would be by running it.
            await using (var db = new HostPingerDbContext(options))
            {
                await HostPingerDatabase.InitializeAsync(db);
            }

            var settings = await ReadSettingsAsync(configuration, options);
            Assert.That(settings.MaxDatabaseSizeMb, Is.GreaterThan(0),
                "the fill is sized as a fraction of the maximum, so there has to be one");
            Assert.That(settings.IntervalSeconds, Is.GreaterThan(0),
                "the fake pings are spaced by the configured interval");

            var interval = TimeSpan.FromSeconds(settings.IntervalSeconds);
            var targetBytes = (long)(settings.MaxDatabaseSizeBytes * FillFraction);
            Report($"Settings   interval {settings.IntervalSeconds}s, "
                + $"retries {settings.RetryAttempts}, max {settings.MaxDatabaseSizeMb} MB");
            Report($"Target     {Megabytes(targetBytes)} ({FillFraction:P0} of the maximum)");

            long baselineBytes;
            int hostId;
            await using (var db = new HostPingerDbContext(options))
            {
                hostId = await EnsureSeedHostAsync(db);
                var deleted = await db.PingAttempts.Where(a => a.HostId == hostId).ExecuteDeleteAsync();

                // Deleted pages are only returned to the file by an explicit vacuum, and until they
                // are, the size below would still be counting the history that was just dropped.
                await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;");
                baselineBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
                Report($"Cleared    {deleted:N0} attempts of {SeedHostName} ({SeedHostAddress}), "
                    + $"leaving {Megabytes(baselineBytes)}");
            }

            if (baselineBytes >= targetBytes)
            {
                Assert.Ignore($"the database is already {Megabytes(baselineBytes)} without this host's "
                    + $"history, which is past the {Megabytes(targetBytes)} being filled to");
            }

            var bytesPerRow = await MeasureBytesPerRowAsync(interval);
            var rows = (int)Math.Min(int.MaxValue, (targetBytes - baselineBytes) / bytesPerRow);
            Report($"Calibrated {bytesPerRow:N1} bytes a row, so {rows:N0} rows to write");

            var plan = FillPlan.Centered(hostId, rows, interval, EndOfFakeHistory());
            Report($"Period     {plan.StartUtc:u} to {plan.EndUtc:u} ({(plan.EndUtc - plan.StartUtc).TotalDays:N1} days)");
            Report($"Outage     {plan.DownStartUtc:u} to {plan.DownEndUtc:u} "
                + $"({plan.DownRows:N0} rows, {DownFraction:P0} of the period)");

            var stopwatch = Stopwatch.StartNew();
            await InsertAttemptsAsync(paths.DatabasePath, plan,
                written => Report($"  wrote {written:N0} of {plan.Rows:N0} rows"));
            Report($"Filled     {plan.Rows:N0} rows in {stopwatch.Elapsed.TotalSeconds:N1}s");

            long finalBytes;
            await using (var db = new HostPingerDbContext(options))
            {
                // The history now reaches back further than the host row claims to have existed,
                // which is only true because it was written rather than recorded.
                await db.Hosts.Where(h => h.Id == hostId && h.CreatedUtc > plan.StartUtc)
                    .ExecuteUpdateAsync(h => h.SetProperty(x => x.CreatedUtc, plan.StartUtc));

                await ClearQueryPlannerStatisticsAsync(db);
                finalBytes = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
            }

            Report($"Database   {Megabytes(finalBytes)}, "
                + $"{(double)finalBytes / settings.MaxDatabaseSizeBytes:P1} of the maximum");

            await VerifyAsync(options, settings, plan);

            Assert.That(finalBytes, Is.EqualTo(targetBytes).Within(5).Percent,
                "the fill should land near the fraction of the maximum it was sized to");
        }

        /// <summary>
        /// The settings as the application reads them: the configured values, under whatever rows
        /// the UserSettings table of this very database holds — so a database filled at the
        /// interval shown on the Configuration page is filled at the interval it is actually
        /// pinged at.
        /// </summary>
        private static async Task<PingerOptions> ReadSettingsAsync(
            IConfiguration configuration,
            DbContextOptions<HostPingerDbContext> options)
        {
            var store = new UserSettingsStore(
                new TestDb.Factory(options),
                new TestOptionsMonitor<PingerOptions>(
                    configuration.GetSection(PingerOptions.SectionName).Get<PingerOptions>() ?? new PingerOptions()),
                new TestOptionsMonitor<SecurityOptions>(new SecurityOptions()));
            await store.LoadAsync();
            return store.CurrentPinger;
        }

        /// <summary>
        /// Settles on the one host this fixture owns, and returns its id. A row is claimed by
        /// either half of its identity — the name or the address — and the claimed row is then
        /// rewritten to both.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Matching on the address alone is what a unique index invites and it is wrong here: a
        /// database that already holds a <c>localhost</c> pointing at something else keeps it, and
        /// gains a second host of the same name beside it. The Hosts page lists both, in name
        /// order, with nothing on the row to say which is the seeded one.
        /// </para>
        /// <para>
        /// So every claimant is collected instead. The lowest id wins, because that is the row
        /// that has been there and the one whatever is bookmarked or linked already points at, and
        /// the others are deleted with their history. Deleting is the point rather than a
        /// side-effect: this fixture is asked to drop what the database holds for localhost, and a
        /// host that answers to the name is part of what it holds.
        /// </para>
        /// </remarks>
        private static async Task<int> EnsureSeedHostAsync(HostPingerDbContext db)
        {
            var claimants = await db.Hosts
                .Where(h => h.Name == SeedHostName || h.Address == SeedHostAddress)
                .OrderBy(h => h.Id)
                .ToListAsync();

            if (claimants is [])
            {
                var added = new MonitoredHost
                {
                    Name = SeedHostName,
                    Address = SeedHostAddress,
                    IsEnabled = true,
                    CreatedUtc = DateTime.UtcNow,
                };
                db.Hosts.Add(added);
                await db.SaveChangesAsync();
                Report($"Added      {SeedHostName} ({SeedHostAddress}) as host {added.Id}");
                return added.Id;
            }

            var host = claimants[0];
            foreach (var duplicate in claimants.Skip(1))
            {
                // Ahead of the host row rather than left to the cascade, so the attempts go as one
                // statement per host instead of as a delete the foreign key walks a row at a time.
                var dropped = await db.PingAttempts.Where(a => a.HostId == duplicate.Id).ExecuteDeleteAsync();
                Report($"Dropped    duplicate host {duplicate.Id} "
                    + $"({duplicate.Name} / {duplicate.Address}) and its {dropped:N0} attempts");
            }

            db.Hosts.RemoveRange(claimants.Skip(1));
            host.Name = SeedHostName;
            host.Address = SeedHostAddress;
            host.IsEnabled = true;
            await db.SaveChangesAsync();

            Report($"Claimed    host {host.Id} as {SeedHostName} ({SeedHostAddress})");
            return host.Id;
        }

        /// <summary>
        /// Writes the planned attempts straight through ADO.NET. EF would do the same job, but a
        /// fill this size is millions of rows and the change tracker has nothing to offer any of
        /// them: one prepared statement re-executed against a transaction a batch at a time is
        /// most of an order of magnitude quicker, and quick enough that the fill is not what
        /// anybody is waiting on.
        /// </summary>
        private static async Task InsertAttemptsAsync(
            string databasePath,
            FillPlan plan,
            Action<int>? onBatchWritten = null)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO PingAttempts (HostId, TimestampUtc, RoundtripMs) VALUES ($hostId, $timestampUtc, $roundtripMs);";
            var hostId = command.Parameters.Add("$hostId", SqliteType.Integer);
            var timestampUtc = command.Parameters.Add("$timestampUtc", SqliteType.Text);
            var roundtripMs = command.Parameters.Add("$roundtripMs", SqliteType.Integer);
            hostId.Value = plan.HostId;

            for (var offset = 0; offset < plan.Rows; offset += InsertBatchRows)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
                command.Transaction = transaction;

                var end = Math.Min(offset + InsertBatchRows, plan.Rows);
                for (var row = offset; row < end; row++)
                {
                    timestampUtc.Value = plan.TimestampOf(row).ToString(SqliteDateTimeFormat, CultureInfo.InvariantCulture);
                    roundtripMs.Value = plan.IsDown(row) ? DBNull.Value : UpRoundtripMs;
                    await command.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                onBatchWritten?.Invoke(end);
            }
        }

        /// <summary>
        /// Measures what a row of this shape costs on disk, by writing a sample of them into an
        /// empty database carrying the same schema and dividing. The alternative is arithmetic over
        /// column widths and index entries, which would be wrong the first time an index is added.
        /// </summary>
        /// <remarks>
        /// A scratch file rather than the database being filled, so the only thing ever written to
        /// that one is the history it is meant to end up with. The sample carries the same mix of
        /// answered and unanswered rows as the fill, because the partial index over the unanswered
        /// ones is real bytes that only they pay for.
        /// </remarks>
        private static async Task<double> MeasureBytesPerRowAsync(TimeSpan interval)
        {
            var path = Path.Combine(Path.GetTempPath(), $"hostpinger-calibration-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<HostPingerDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            try
            {
                int hostId;
                long before;
                await using (var db = new HostPingerDbContext(options))
                {
                    await HostPingerDatabase.EnableIncrementalVacuumAsync(db);
                    await db.Database.EnsureCreatedAsync();
                    var host = new MonitoredHost
                    {
                        Name = SeedHostName,
                        Address = SeedHostAddress,
                        CreatedUtc = DateTime.UtcNow,
                    };
                    db.Hosts.Add(host);
                    await db.SaveChangesAsync();
                    hostId = host.Id;
                    before = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
                }

                await InsertAttemptsAsync(
                    path,
                    FillPlan.Centered(hostId, CalibrationRows, interval, EndOfFakeHistory()));

                await using (var db = new HostPingerDbContext(options))
                {
                    var after = await DatabasePruner.GetDatabaseSizeBytesAsync(db);
                    return (double)(after - before) / CalibrationRows;
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                File.Delete(path);
            }
        }

        /// <summary>
        /// Reads the filled history back the way the Hosts page does, both to prove the rows went
        /// in as the application will read them — they were written as raw text, so the timestamp
        /// format is worth confirming rather than assuming — and to put a number on what that page
        /// now costs, which is the point of the whole exercise.
        /// </summary>
        private static async Task VerifyAsync(
            DbContextOptions<HostPingerDbContext> options,
            PingerOptions settings,
            FillPlan plan)
        {
            await using var db = new HostPingerDbContext(options);
            var attempts = await db.PingAttempts.CountAsync(a => a.HostId == plan.HostId);
            var unanswered = await db.PingAttempts.CountAsync(a => a.HostId == plan.HostId && a.RoundtripMs == null);

            var stopwatch = Stopwatch.StartNew();
            var summaries = await HostSummary.LoadAsync(db, settings.RetryAttempts);
            stopwatch.Stop();
            Report($"Hosts page {stopwatch.Elapsed.TotalMilliseconds:N0}ms for {summaries.Count} host(s)");

            var seeded = summaries.Single(s => s.Host.Id == plan.HostId);
            Assert.Multiple(() =>
            {
                Assert.That(attempts, Is.EqualTo(plan.Rows), "every planned attempt should be readable");
                Assert.That(unanswered, Is.EqualTo(plan.DownRows), "the outage should be the planned half");
                Assert.That(seeded.Status, Is.EqualTo(HostStatus.Up), "the history ends with the host answering again");

                // Both ends of a downtime are answered pings, so the outage the page reports is the
                // written one widened by the interval either side of it. That it lands exactly
                // there is what says the raw rows sort and compare as EF's own would.
                Assert.That(seeded.LastDowntime, Is.Not.Null);
                Assert.That(seeded.LastDowntime!.StartedUtc, Is.EqualTo(plan.DownStartUtc - plan.Interval));
                Assert.That(seeded.LastDowntime.EndedUtc, Is.EqualTo(plan.DownEndUtc + plan.Interval));
            });
        }

        /// <summary>
        /// Throws away the query planner's statistics rather than recomputing them. The application
        /// never runs ANALYZE, so an installed database plans every query on SQLite's defaults, and
        /// a development database measured with statistics — worse, with the stale statistics of
        /// whatever it held before this fill — is not measuring the same plans.
        /// </summary>
        private static async Task ClearQueryPlannerStatisticsAsync(HostPingerDbContext db)
        {
            var exists = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1'")
                .SingleAsync();
            if (exists == 0)
            {
                return;
            }

            await db.Database.ExecuteSqlRawAsync("DELETE FROM sqlite_stat1;");

            // Reloads the emptied table into the connection's in-memory statistics without
            // recomputing anything, which is what ANALYZE over a plain table would do.
            await db.Database.ExecuteSqlRawAsync("ANALYZE sqlite_schema;");
            Report("Cleared    the query planner statistics, so plans match an installed database");
        }

        /// <summary>
        /// The last fake ping, on a whole second so that every timestamp written is one — which is
        /// how they end up byte-identical to what EF would have written for the same instant.
        /// </summary>
        private static DateTime EndOfFakeHistory()
        {
            var now = DateTime.UtcNow;
            return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        }

        /// <summary>
        /// The application's content root, found by walking up to the solution file. The
        /// development database is configured relative to it, so this is what decides which
        /// database gets filled.
        /// </summary>
        private static string FindApplicationContentRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (directory.GetFiles("HostPinger.slnx").Length > 0)
                {
                    return Path.Combine(directory.FullName, "HostPinger");
                }
            }

            throw new InvalidOperationException(
                $"No HostPinger.slnx above {AppContext.BaseDirectory}; this fixture fills the database "
                + "configured by the repository's appsettings.Development.json and has to be run from a checkout.");
        }

        /// <summary>
        /// The one place this fixture could do real damage is a machine where it resolves the path
        /// an installed service keeps its history in. Nothing in a checkout points there — the
        /// development settings name a file under the content root — so reaching it means the
        /// development settings were missing or an environment variable overrode them, and either
        /// way this should stop rather than delete somebody's recorded months.
        /// </summary>
        private static void GuardAgainstTheInstalledDatabase(PingerPaths paths)
        {
            var installed = Path.GetFullPath(
                Path.Combine(PingerPaths.DefaultDataDirectory, PingerPaths.DatabaseFileName));
            Assert.That(paths.DatabasePath, Is.Not.EqualTo(installed),
                "this resolves to the database an installed service uses, which this fixture will not rewrite; "
                + "check that appsettings.Development.json is present and that no Pinger__DatabasePath is set");
        }

        private static string Megabytes(long bytes) =>
            $"{bytes / 1024.0 / 1024.0:N1} MB";

        /// <summary>
        /// Progress rather than the result output, because a fill of this size takes long enough
        /// that a line arriving when it happens is worth more than the whole log arriving at the
        /// end.
        /// </summary>
        private static void Report(string line) => TestContext.Progress.WriteLine(line);

        /// <summary>
        /// The history to write, as a row count and where the outage sits inside it. Every
        /// timestamp is derived from the row index, so the plan is what both the fill and the
        /// checks afterwards read their expectations from.
        /// </summary>
        /// <param name="HostId">The host the attempts belong to.</param>
        /// <param name="StartUtc">The first attempt.</param>
        /// <param name="Interval">The spacing between attempts, from the settings.</param>
        /// <param name="Rows">How many attempts in total.</param>
        /// <param name="FirstDownRow">The first unanswered attempt.</param>
        /// <param name="DownRows">How many attempts the outage covers.</param>
        private sealed record FillPlan(
            int HostId,
            DateTime StartUtc,
            TimeSpan Interval,
            int Rows,
            int FirstDownRow,
            int DownRows)
        {
            public DateTime EndUtc => TimestampOf(Rows - 1);

            public DateTime DownStartUtc => TimestampOf(FirstDownRow);

            public DateTime DownEndUtc => TimestampOf(FirstDownRow + DownRows - 1);

            /// <summary>
            /// An outage of <see cref="DownFraction"/> of the attempts with the same number of
            /// answered ones either side of it, ending at <paramref name="endUtc"/> so the host
            /// reads as up now and the monitor can carry straight on from there.
            /// </summary>
            public static FillPlan Centered(int hostId, int rows, TimeSpan interval, DateTime endUtc)
            {
                var downRows = (int)Math.Round(rows * DownFraction);
                return new FillPlan(
                    hostId,
                    endUtc - interval * (rows - 1),
                    interval,
                    rows,
                    (rows - downRows) / 2,
                    downRows);
            }

            public DateTime TimestampOf(int row) => StartUtc + Interval * row;

            public bool IsDown(int row) => row >= FirstDownRow && row < FirstDownRow + DownRows;
        }
    }
}
