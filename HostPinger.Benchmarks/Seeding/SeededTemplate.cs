using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Benchmarks.Seeding
{
    /// <summary>
    /// The seeded database every benchmark copies from: created the way the application creates one,
    /// filled with <see cref="SyntheticHistory"/>, checked, and kept on disk so that only the first run
    /// after a change pays for the minute or two it takes to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is named for the schema's latest migration and <see cref="SyntheticHistory.Fingerprint"/>,
    /// so a new migration or any change to what is planned seeds a new one, and the old one is deleted
    /// when it does. It is built under a name of its own and only moved into place once checked, so a
    /// run that is stopped halfway leaves nothing behind that a later run would take for finished.
    /// </para>
    /// <para>
    /// It lives under the benchmark project's bin directory unless HOSTPINGER_BENCHMARK_DATA names
    /// somewhere else. Not the system temporary directory: on many Linux machines that is a tmpfs,
    /// which keeps a gigabyte in memory twice over and, worse, makes every read look free — the cost
    /// that mattered in production was reading pages from disk.
    /// </para>
    /// </remarks>
    internal static class SeededTemplate
    {
        public const string DataDirectoryVariable = "HOSTPINGER_BENCHMARK_DATA";

        /// <summary>The format EF Core's SQLite provider stores a DateTime in; see DevDatabaseFillTests.</summary>
        private const string SqliteDateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        /// <summary>Rounds per transaction: about four hundred thousand rows.</summary>
        private const int RoundsPerTransaction = 20_000;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Every host's rounds, planned once per process: they name the template as well as fill it.</summary>
        private static readonly Lazy<RoundState[][]> Plans =
            new(() => Enumerable.Range(0, SyntheticHistory.Hosts.Count).Select(SyntheticHistory.Plan).ToArray());

        private static readonly Lazy<string> Template = new(TemplatePath);

        public static string DataDirectory { get; } = ResolveDataDirectory();

        /// <summary>
        /// The template's path, seeding it first if it is not there yet or <paramref name="rebuild"/>
        /// says to.
        /// </summary>
        public static async Task<string> EnsureAsync(TextWriter log, bool rebuild = false)
        {
            var path = Template.Value;
            if (!rebuild && File.Exists(path) && File.Exists(FactsPath(path)))
            {
                return path;
            }

            Directory.CreateDirectory(DataDirectory);
            foreach (var stale in Directory.EnumerateFiles(DataDirectory, "template-*"))
            {
                File.Delete(stale);
            }

            var building = Path.Combine(DataDirectory, $"building-{Environment.ProcessId}.db");
            try
            {
                var facts = await BuildAsync(building, log);

                // Closed before the move: a pooled connection still holds the file open, and Windows
                // refuses to rename a file SQLite has open.
                SqliteConnection.ClearAllPools();
                File.Move(building, path, overwrite: true);
                await File.WriteAllTextAsync(FactsPath(path), JsonSerializer.Serialize(facts, JsonOptions));
                log.WriteLine($"Template   {path}");
                return path;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                File.Delete(building);
                File.Delete(building + "-journal");
            }
        }

        /// <summary>What the template holds, written beside it when it was seeded; null if there is none.</summary>
        public static SeedFacts? ReadFacts()
        {
            var factsPath = FactsPath(Template.Value);
            return File.Exists(factsPath)
                ? JsonSerializer.Deserialize<SeedFacts>(File.ReadAllText(factsPath), JsonOptions)
                : null;
        }

        private static async Task<SeedFacts> BuildAsync(string path, TextWriter log)
        {
            var stopwatch = Stopwatch.StartNew();
            log.WriteLine($"Seeding    {path}");

            // Created the way the application creates a database at startup, auto-vacuum and all, so
            // the file has the application's schema and page layout rather than one written to match.
            var options = HostPingerDatabase.Options(path);
            await using (var db = new HostPingerDbContext(options))
            {
                await HostPingerDatabase.InitializeAsync(db);

                var hostsFrom = SyntheticHistory.RoundTimeUtc(0) - TimeSpan.FromDays(17);
                for (var i = 0; i < SyntheticHistory.Hosts.Count; i++)
                {
                    var pattern = SyntheticHistory.Hosts[i];
                    db.Hosts.Add(new MonitoredHost
                    {
                        Id = i + 1,
                        Name = pattern.Name,
                        Address = pattern.Address,
                        IsEnabled = true,
                        CreatedUtc = pattern.AddedHoursBeforeEnd is double added
                            ? SyntheticHistory.RoundTimeUtc(SyntheticHistory.RoundAt(added)) - TimeSpan.FromSeconds(5)
                            : hostsFrom,
                    });
                }

                // The rows production's database held. The size limit was not among them, so it is
                // the default here as it was there.
                foreach (var (name, value) in new[]
                {
                    (nameof(PingerOptions.IntervalSeconds), SyntheticHistory.IntervalSeconds),
                    (nameof(PingerOptions.TimeoutSeconds), SyntheticHistory.TimeoutSeconds),
                    (nameof(PingerOptions.ResolveTimeoutSeconds), SyntheticHistory.ResolveTimeoutSeconds),
                    (nameof(PingerOptions.RetryAttempts), SyntheticHistory.RetryAttempts),
                })
                {
                    await UserSettingsStore.StageAsync(
                        db,
                        $"{PingerOptions.SectionName}:{name}",
                        value.ToString(CultureInfo.InvariantCulture));
                }

                await db.SaveChangesAsync();
            }

            var plans = Plans.Value;
            var (attempts, resolverErrors) = WriteHistory(path, plans, log);
            log.WriteLine($"Wrote      {attempts:N0} attempts and {resolverErrors:N0} resolver errors in {stopwatch.Elapsed.TotalSeconds:N0}s");

            SqliteConnection.ClearAllPools();
            var facts = await VerifyAsync(options, plans, attempts, resolverErrors, new FileInfo(path).Length);
            log.WriteLine($"Checked    {facts.Hosts.Count} hosts, {facts.FileBytes / 1024.0 / 1024.0:N0} MB, "
                + $"a summary walk of {facts.SummaryWalk} (production's was {SyntheticHistory.ProductionSummaryWalk}), "
                + $"in {stopwatch.Elapsed.TotalSeconds:N0}s overall");
            return facts;
        }

        /// <summary>
        /// Writes every round in the order the application would have, so the attempts' ids rise with
        /// time and each round's rows sit together in the table, as they do in a recorded one. Straight
        /// through ADO.NET rather than EF, for the reason DevDatabaseFillTests gives, and with the
        /// journal and syncing off, since a file that fails halfway is deleted rather than recovered.
        /// </summary>
        private static (long Attempts, long ResolverErrors) WriteHistory(string path, RoundState[][] plans, TextWriter log)
        {
            using var connection = new SqliteConnection(HostPingerDatabase.ConnectionString(path));
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA cache_size = -262144;";
                pragmas.ExecuteNonQuery();
            }

            using var insertAttempt = connection.CreateCommand();
            insertAttempt.CommandText =
                "INSERT INTO PingAttempts (HostId, TimestampUtc, RoundtripMs) VALUES ($hostId, $timestampUtc, $roundtripMs);";
            var attemptHost = insertAttempt.Parameters.Add("$hostId", SqliteType.Integer);
            var attemptTime = insertAttempt.Parameters.Add("$timestampUtc", SqliteType.Text);
            var attemptRoundtrip = insertAttempt.Parameters.Add("$roundtripMs", SqliteType.Integer);

            using var insertError = connection.CreateCommand();
            insertError.CommandText =
                "INSERT INTO ResolverErrors (Address, TimestampUtc, Reason) VALUES ($address, $timestampUtc, $reason);";
            var errorAddress = insertError.Parameters.Add("$address", SqliteType.Text);
            var errorTime = insertError.Parameters.Add("$timestampUtc", SqliteType.Text);
            insertError.Parameters.Add("$reason", SqliteType.Integer).Value = (int)ResolverFailure.TimedOut;

            // The pruner deletes resolver errors past their retention every round, so only the ones the
            // last round would have left are written.
            var firstKeptError = SyntheticHistory.RoundAt(ResolverError.Retention.TotalHours);

            var hosts = SyntheticHistory.Hosts;
            var roundtrips = hosts.Select((_, i) => new Random(2_000 + i)).ToArray();
            long attempts = 0;
            long resolverErrors = 0;
            for (var batchStart = 0; batchStart < SyntheticHistory.RoundCount; batchStart += RoundsPerTransaction)
            {
                using var transaction = connection.BeginTransaction();
                insertAttempt.Transaction = transaction;
                insertError.Transaction = transaction;

                var batchEnd = Math.Min(batchStart + RoundsPerTransaction, SyntheticHistory.RoundCount);
                for (var round = batchStart; round < batchEnd; round++)
                {
                    var timestamp = SyntheticHistory.RoundTimeUtc(round).ToString(SqliteDateTimeFormat, CultureInfo.InvariantCulture);
                    attemptTime.Value = timestamp;
                    for (var i = 0; i < hosts.Count; i++)
                    {
                        switch (plans[i][round])
                        {
                            case RoundState.Answered:
                            case RoundState.Missed:
                                attemptHost.Value = i + 1;
                                attemptRoundtrip.Value = plans[i][round] == RoundState.Answered
                                    ? hosts[i].Roundtrip.Sample(roundtrips[i])
                                    : DBNull.Value;
                                insertAttempt.ExecuteNonQuery();
                                attempts++;
                                break;

                            case RoundState.Unresolved when round >= firstKeptError:
                                errorAddress.Value = hosts[i].Address;
                                errorTime.Value = timestamp;
                                insertError.ExecuteNonQuery();
                                resolverErrors++;
                                break;
                        }
                    }
                }

                transaction.Commit();
                log.WriteLine($"  {batchEnd:N0} of {SyntheticHistory.RoundCount:N0} rounds");
            }

            return (attempts, resolverErrors);
        }

        /// <summary>
        /// Reads the seeded file back the way the application would and holds it to the plan: every
        /// row went in, and the Hosts summary finds each host where its history leaves it, the two
        /// long outages still running. The rows were written as raw text, so this is also what
        /// proves the timestamps sort and compare the way EF's own do. It is held to production, too,
        /// in the two ways the benchmarks would quietly stop meaning anything: the summary's walk, and
        /// the file staying under the size limit, past which every round the benchmarks land would
        /// prune the history they are reading.
        /// </summary>
        private static async Task<SeedFacts> VerifyAsync(
            DbContextOptions<HostPingerDbContext> options,
            RoundState[][] plans,
            long attempts,
            long resolverErrors,
            long fileBytes)
        {
            await using var db = new HostPingerDbContext(options);
            var storedAttempts = await db.PingAttempts.LongCountAsync();
            var storedErrors = await db.ResolverErrors.LongCountAsync();
            if (storedAttempts != attempts || storedErrors != resolverErrors)
            {
                throw new InvalidOperationException(
                    $"Wrote {attempts:N0} attempts and {resolverErrors:N0} resolver errors, read back {storedAttempts:N0} and {storedErrors:N0}.");
            }

            var nowUtc = SyntheticHistory.LastRoundUtc;
            var summaries = await HostSummary.LoadAsync(db, SyntheticHistory.RetryAttempts);
            var hosts = new List<SeededHost>();
            for (var i = 0; i < SyntheticHistory.Hosts.Count; i++)
            {
                var pattern = SyntheticHistory.Hosts[i];
                var plan = plans[i];
                var summary = summaries.Single(s => s.Host.Name == pattern.Name);
                var expected = ExpectedStatus(plan);
                if (summary.Status != expected)
                {
                    throw new InvalidOperationException($"{pattern.Name} reads as {summary.Status}, where its history leaves it {expected}.");
                }

                hosts.Add(new SeededHost(
                    pattern.Name,
                    pattern.Kind.ToString(),
                    plan.LongCount(state => state is RoundState.Answered or RoundState.Missed),
                    plan.LongCount(state => state is RoundState.Missed),
                    summary.Status.ToString(),
                    summary.LastDowntime is { } downtime
                        ? (downtime.IsOngoing ? "ongoing, " : string.Empty) + Downtime.FormatDuration(downtime.DurationAt(nowUtc))
                        : null,
                    SyntheticHistory.SummaryWalk(plan)));
            }

            var walk = hosts.Sum(h => h.SummaryWalk);
            if (Math.Abs(walk - SyntheticHistory.ProductionSummaryWalk) > SyntheticHistory.ProductionSummaryWalk * 0.15)
            {
                throw new InvalidOperationException(
                    $"The Hosts summary steps over {walk} missed pings here and {SyntheticHistory.ProductionSummaryWalk} in production; "
                    + "it is the part of that read the history decides, so the seeding has drifted from production's.");
            }

            // Eight megabytes is some three thousand rounds, where a run lands at most a few dozen on
            // any one copy.
            var limit = new PingerOptions().MaxDatabaseSizeBytes;
            if (fileBytes > limit - (8 * 1024 * 1024))
            {
                throw new InvalidOperationException(
                    $"The template is {fileBytes:N0} bytes, too close to the {limit:N0} byte limit: the rounds the benchmarks land would prune it.");
            }

            var longest = summaries.Single(s => s.Host.Name == SyntheticHistory.LongestOutage).LastDowntime;
            if (longest is not { IsOngoing: true } || longest.DurationAt(nowUtc) < TimeSpan.FromDays(40))
            {
                throw new InvalidOperationException($"{SyntheticHistory.LongestOutage} should be forty-one days into an outage.");
            }

            return new SeedFacts(
                SyntheticHistory.Fingerprint(plans),
                SyntheticHistory.RoundTimeUtc(0),
                SyntheticHistory.LastRoundUtc,
                SyntheticHistory.IntervalSeconds,
                fileBytes,
                attempts,
                hosts.Sum(h => h.Missed),
                resolverErrors,
                walk,
                hosts);
        }

        /// <summary>
        /// Where the last recorded rounds leave a host: up if its last ping was answered, down once
        /// more pings than the retries allow have gone unanswered since, retrying before that.
        /// </summary>
        private static HostStatus ExpectedStatus(RoundState[] plan)
        {
            var missedSince = 0;
            for (var round = plan.Length - 1; round >= 0; round--)
            {
                switch (plan[round])
                {
                    case RoundState.Answered:
                        return missedSince == 0
                            ? HostStatus.Up
                            : missedSince > SyntheticHistory.RetryAttempts ? HostStatus.Down : HostStatus.Retrying;
                    case RoundState.Missed:
                        missedSince++;
                        break;
                }
            }

            return missedSince == 0 ? HostStatus.Waiting : HostStatus.Down;
        }

        private static string TemplatePath()
        {
            // Read off the migrations the build carries, which needs no database: a schema change is a
            // new migration, and a new migration is a template seeded under the new schema.
            using var db = new HostPingerDbContext(HostPingerDatabase.Options("unopened.db"));
            var latestMigration = db.Database.GetMigrations().Last();
            return Path.Combine(DataDirectory, $"template-{latestMigration}-{SyntheticHistory.Fingerprint(Plans.Value)}.db");
        }

        private static string FactsPath(string templatePath) => Path.ChangeExtension(templatePath, ".json");

        private static string ResolveDataDirectory()
        {
            if (Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } configured)
            {
                return Path.GetFullPath(configured);
            }

            // Walked up to from wherever the code runs, which for a benchmark is a directory
            // BenchmarkDotNet generates below this project's bin; every process it starts finds the
            // same directory this way.
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (directory.GetFiles("HostPinger.slnx").Length > 0)
                {
                    return Path.Combine(directory.FullName, "HostPinger.Benchmarks", "bin", "BenchmarkData");
                }
            }

            throw new InvalidOperationException(
                $"No HostPinger.slnx above {AppContext.BaseDirectory}; set {DataDirectoryVariable} to a directory on disk to seed the benchmark database in.");
        }
    }

    /// <summary>What a seeded template holds, for the report to say what the benchmarks ran against.</summary>
    internal sealed record SeedFacts(
        string Fingerprint,
        DateTime HistoryStartUtc,
        DateTime HistoryEndUtc,
        int IntervalSeconds,
        long FileBytes,
        long Attempts,
        long Missed,
        long ResolverErrors,
        int SummaryWalk,
        IReadOnlyList<SeededHost> Hosts);

    /// <summary>One host of a seeded template, as the Hosts page would show it at the end of the history.</summary>
    internal sealed record SeededHost(
        string Name,
        string Kind,
        long Attempts,
        long Missed,
        string Status,
        string? LastDowntime,
        int SummaryWalk);
}
