using Microsoft.Extensions.Logging;

namespace HostPinger.Test
{
    /// <summary>
    /// A logger that keeps what it is given, for services whose log is part of what they promise:
    /// a failure the user only sees as a message, a loop that reports a run of failures once.
    /// Safe to read while a background loop is still writing to it.
    /// </summary>
    internal sealed class TestLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
