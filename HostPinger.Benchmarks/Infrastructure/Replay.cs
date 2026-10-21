using HostPinger.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Benchmarks.Infrastructure
{
    /// <summary>
    /// Answers every ping the way the host's last recorded one went, so the rounds a benchmark lands
    /// carry each history on as it was: the hosts that are down stay down, and the rest answer with
    /// the round trip they last had. No packet leaves the machine.
    /// </summary>
    internal sealed class ReplayPingSender : IPingSender
    {
        private Dictionary<string, PingResult> _lastResults = [];

        public void ReplayLastOf(HostSummarySnapshot summary) =>
            _lastResults = summary.Rows.ToDictionary(
                row => row.Host.Address,
                row => row.Last?.RoundtripMs is int roundtripMs ? PingResult.Answered(roundtripMs) : PingResult.Unanswered);

        public Task<PingResult> SendPingAsync(
            string address,
            int timeoutMilliseconds,
            int resolveTimeoutMilliseconds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_lastResults.TryGetValue(address, out var result) ? result : PingResult.Unanswered);
    }

    /// <summary>
    /// A clock that stands still until it is moved, one ping interval per round, starting from the end
    /// of the seeded history rather than from today.
    /// </summary>
    internal sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Writes HostPinger's own warnings and errors to the console, where BenchmarkDotNet keeps them in
    /// its log. Without this a failing read in the summary's loop would be swallowed, and the benchmark
    /// would go on timing the summary in hand instead of the read it means to time. EF Core's are left
    /// out: the one it gives is about Core's queries, which the application logs too, and it would
    /// otherwise open every benchmark's log.
    /// </summary>
    internal sealed class ConsoleWarnings : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("HostPinger", StringComparison.Ordinal) ? this : NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Console.WriteLine($"{logLevel}: {formatter(state, exception)}{(exception is null ? string.Empty : $" {exception}")}");
            }
        }

        public void Dispose()
        {
        }
    }
}
