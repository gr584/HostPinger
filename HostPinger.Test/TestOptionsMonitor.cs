using Microsoft.Extensions.Options;

namespace HostPinger.Test
{
    /// <summary>
    /// A monitor over one value, for services that read <c>CurrentValue</c>. Setting the value
    /// stands in for the configuration being reloaded underneath such a service, and tells anything
    /// listening, the way the real monitor does.
    /// </summary>
    internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];

        private T _current = value;

        public T CurrentValue
        {
            get => _current;
            set
            {
                _current = value;

                // Over a copy: a listener is free to unsubscribe as it runs, which is what the gate
                // does the moment the value it was waiting for arrives.
                foreach (var listener in _listeners.ToArray())
                {
                    listener(value, null);
                }
            }
        }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new Subscription(() => _listeners.Remove(listener));
        }

        private sealed class Subscription(Action unsubscribe) : IDisposable
        {
            public void Dispose() => unsubscribe();
        }
    }
}
