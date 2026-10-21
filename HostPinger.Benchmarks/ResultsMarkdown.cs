using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using HostPinger.Benchmarks.Benchmarks;
using HostPinger.Benchmarks.Seeding;

namespace HostPinger.Benchmarks
{
    /// <summary>
    /// One Markdown page out of a whole run: what the benchmarks ran against, then a table per page
    /// under a heading saying what it measures, and the environment once at the end. CI appends it to
    /// the job's summary, which is where a push's results are read.
    /// </summary>
    /// <remarks>
    /// BenchmarkDotNet's own GitHub report is one file per class, each opening with the same
    /// environment block and that class's job settings; the tables here are those reports with that
    /// block taken off. Since the job is not the same for every class, each table says instead how many
    /// calls it timed, counted from what actually ran.
    /// </remarks>
    internal static class ResultsMarkdown
    {
        public const string FileName = "summary.md";

        private static readonly Regex JobSettings = new(@"^\w+=\S", RegexOptions.Compiled);

        public static string Write(IReadOnlyList<Summary> summaries, SeedFacts? facts)
        {
            var markdown = new StringBuilder();
            markdown.AppendLine("## Database benchmarks").AppendLine();
            if (facts is not null)
            {
                AppendSeed(markdown, facts);
            }

            string? environment = null;
            foreach (var summary in summaries.Where(s => s.BenchmarksCases.Length > 0))
            {
                var (header, table) = Split(Export(summary));
                environment ??= header;

                var type = summary.BenchmarksCases[0].Descriptor.Type;
                var page = type.GetCustomAttribute<PageBenchmarkAttribute>();
                markdown.AppendLine($"### {page?.Heading ?? type.Name}").AppendLine();
                if (page is not null)
                {
                    markdown.AppendLine(page.Description).AppendLine();
                }

                markdown.AppendLine(table.Trim()).AppendLine();
                if (Iterations(summary) is { } iterations)
                {
                    markdown.AppendLine(iterations).AppendLine();
                }
            }

            if (environment is not null)
            {
                markdown.AppendLine("<details><summary>Environment</summary>").AppendLine()
                    .AppendLine(environment.Trim()).AppendLine()
                    .AppendLine("</details>");
            }

            var path = Path.Combine(summaries[0].ResultsDirectoryPath, FileName);
            File.WriteAllText(path, markdown.ToString());
            return path;
        }

        private static void AppendSeed(StringBuilder markdown, SeedFacts facts)
        {
            var days = (facts.HistoryEndUtc - facts.HistoryStartUtc).TotalDays;
            markdown.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Each benchmark starts from a fresh copy of a seeded database shaped like the production one the page freezes were measured on: {facts.Hosts.Count} hosts pinged every {facts.IntervalSeconds} s for {days:N0} days, {facts.Attempts:N0} attempts of which {(double)facts.Missed / facts.Attempts:P1} unanswered, {facts.FileBytes / 1024.0 / 1024.0:N0} MB. Its outages follow production's pattern, two of them still running after 41 and 20 days; none of its names or addresses are production's. Production's resolver errors had been pruned away, so the {facts.ResolverErrors:N0} here are the ones this build records for its outage gaps."))
                .AppendLine();

            markdown.AppendLine("<details><summary>Seeded hosts</summary>").AppendLine();
            markdown.AppendLine("| Host | Addressed by | Attempts | Unanswered | Status | Last downtime |");
            markdown.AppendLine("|---|---|--:|--:|---|---|");
            foreach (var host in facts.Hosts)
            {
                markdown.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {host.Name} | {host.Kind} | {host.Attempts:N0} | {(double)host.Missed / Math.Max(1, host.Attempts):P1} | {host.Status} | {host.LastDowntime ?? "none"} |"));
            }

            markdown.AppendLine().AppendLine("</details>").AppendLine();
        }

        private static string? Iterations(Summary summary)
        {
            var report = summary.Reports.FirstOrDefault(r => r.Success);
            if (report is null)
            {
                return null;
            }

            var workload = report.AllMeasurements.Where(m => m.IterationMode == IterationMode.Workload).ToList();
            var warmup = workload.Count(m => m.IterationStage == IterationStage.Warmup);
            var measured = workload.Count(m => m.IterationStage == IterationStage.Actual);
            return $"_Each row: {warmup} warmup and {measured} measured calls, each timed on its own._";
        }

        private static string Export(Summary summary)
        {
            var logger = new AccumulationLogger();
            MarkdownExporter.GitHub.ExportToLog(summary, logger);
            return logger.GetLog();
        }

        /// <summary>
        /// The environment block the report opens with, inside its code fence, and everything after the
        /// fence: the job's settings and the table.
        /// </summary>
        private static (string Header, string Table) Split(string report)
        {
            var lines = report.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            var opening = lines.FindIndex(line => line.StartsWith("```", StringComparison.Ordinal));
            var closing = opening < 0 ? -1 : lines.FindIndex(opening + 1, line => line.StartsWith("```", StringComparison.Ordinal));
            if (closing < 0)
            {
                return (string.Empty, report);
            }

            // The job's settings, such as "IterationCount=15  WarmupCount=2", are left out of the
            // environment, which is shown once for every table.
            var environment = lines[opening..(closing + 1)].Where(line => !JobSettings.IsMatch(line));
            return (string.Join('\n', environment), string.Join('\n', lines[(closing + 1)..]));
        }
    }
}
