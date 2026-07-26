using Microsoft.Extensions.Options;

namespace HostPinger.Test
{
    /// <summary>A monitor over a fixed value, for services that only read <c>CurrentValue</c>.</summary>
    internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
