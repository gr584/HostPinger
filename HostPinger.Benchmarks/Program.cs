using BenchmarkDotNet.Running;
using HostPinger.Benchmarks.Seeding;

namespace HostPinger.Benchmarks
{
    /// <summary>
    /// Benchmarks of the database calls the pages make through HostPinger.Core, against a seeded copy
    /// of a production-shaped database.
    /// </summary>
    /// <remarks>
    /// <code>
    /// dotnet run -c Release --project HostPinger.Benchmarks -- seed              # seed the template only
    /// dotnet run -c Release --project HostPinger.Benchmarks -- seed --rebuild    # seed it again regardless
    /// dotnet run -c Release --project HostPinger.Benchmarks                      # every benchmark
    /// dotnet run -c Release --project HostPinger.Benchmarks -- --anyCategories Graph
    /// </code>
    /// Anything other than <c>seed</c> goes to BenchmarkDotNet as it is, so its own options apply:
    /// <c>--filter</c>, <c>--list flat</c>, <c>--iterationCount</c> and the rest. With none at all,
    /// every benchmark runs rather than BenchmarkDotNet asking which.
    /// </remarks>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Made absolute before anything reads it: BenchmarkDotNet starts every benchmark in a
            // process of its own, in a directory of its own, and a relative path would name a different
            // directory in each, where the template is not.
            if (Environment.GetEnvironmentVariable(SeededTemplate.DataDirectoryVariable) is { Length: > 0 } configured)
            {
                Environment.SetEnvironmentVariable(SeededTemplate.DataDirectoryVariable, Path.GetFullPath(configured));
            }

            if (args is ["seed", .. var options])
            {
                await SeededTemplate.EnsureAsync(Console.Out, rebuild: options.Contains("--rebuild"));
                return 0;
            }

            // Seeded here rather than in the first benchmark's setup, so that it happens once, in this
            // process, with its progress on the console rather than in a benchmark's log.
            if (!args.Any(arg => arg is "--list" or "--help" or "-h" or "--info" or "--version"))
            {
                await SeededTemplate.EnsureAsync(Console.Out);
            }

            var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(args.Length == 0 ? ["--filter", "*"] : args, BenchmarkConfig.Create())
                .ToList();

            if (summaries.Count > 0)
            {
                var path = ResultsMarkdown.Write(summaries, SeededTemplate.ReadFacts());
                Console.WriteLine($"Summary    {path}");
            }

            // A benchmark that failed is a broken benchmark rather than a slow one, and says so with the
            // exit code; how fast the rest ran is for the reader of the results to judge.
            return summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success))
                ? 1
                : 0;
        }
    }
}
