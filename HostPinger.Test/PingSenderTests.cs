using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HostPinger.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    /// <summary>
    /// Covers what the sender decides between resolving a name and echoing an address: which of the
    /// three outcomes each kind of failure produces, and that nothing is echoed that was never
    /// resolved. The resolver and the echo are injected so none of it depends on the machine's DNS
    /// or on ICMP being permitted, which is what kept this untested while it went through
    /// <see cref="Dns"/> and <see cref="Ping"/> directly.
    /// </summary>
    public class PingSenderTests
    {
        private const int ReplyTimeout = 5_000;
        private const int ResolveTimeout = 3_000;

        /// <summary>An address is already an address; sending it to the resolver could only invent a failure.</summary>
        [Test]
        public async Task SendPing_AnIpLiteralIsNeverResolved()
        {
            var resolver = new FakeResolver();
            var echo = new FakeEcho { Reply = new IcmpReply(IPStatus.Success, 12) };
            var sender = CreateSender(resolver, echo);

            var result = await sender.SendPingAsync("10.0.0.7", ReplyTimeout, ResolveTimeout);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(PingResult.Answered(12)));
                Assert.That(resolver.Calls, Is.Empty, "a literal needs no lookup");
                Assert.That(echo.Destinations, Is.EqualTo(new[] { IPAddress.Parse("10.0.0.7") }));
            });
        }

        /// <summary>
        /// Guards the reason resolution was split out at all: handing the name to Ping would resolve
        /// it a second time, on a wait the reply timeout does not bound.
        /// </summary>
        [Test]
        public async Task SendPing_EchoesTheResolvedAddressRatherThanTheName()
        {
            var resolved = IPAddress.Parse("192.0.2.10");
            var resolver = new FakeResolver { ["host.example"] = [resolved] };
            var echo = new FakeEcho { Reply = new IcmpReply(IPStatus.Success, 12) };
            var sender = CreateSender(resolver, echo);

            var result = await sender.SendPingAsync("host.example", ReplyTimeout, ResolveTimeout);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(PingResult.Answered(12)));
                Assert.That(echo.Destinations, Is.EqualTo(new[] { resolved }));
            });
        }

        /// <summary>
        /// The whole point of the separate timeout: a name that will not come back is abandoned, and
        /// nothing is echoed on its behalf.
        /// </summary>
        [Test]
        public async Task SendPing_ResolutionTimingOutEchoesNothing()
        {
            var resolver = new FakeResolver { Hang = true };
            var echo = new FakeEcho();
            var sender = CreateSender(resolver, echo);

            var result = await sender.SendPingAsync("slow.example", ReplyTimeout, resolveTimeoutMilliseconds: 50);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(PingResult.Unresolved));
                Assert.That(echo.Destinations, Is.Empty, "there was no address to echo");
            });
        }

        /// <summary>
        /// The catch filter that tells the resolve timeout apart from the round being cancelled.
        /// Without it, stopping the service would report every host as unresolvable, and the
        /// shutdown would be swallowed instead of unwinding the round.
        /// </summary>
        [Test]
        public void SendPing_TheRoundBeingCancelledIsNotReportedAsUnresolved()
        {
            var resolver = new FakeResolver { Hang = true };
            var sender = CreateSender(resolver, new FakeEcho());
            using var round = new CancellationTokenSource();

            var pending = sender.SendPingAsync(
                "host.example",
                ReplyTimeout,
                resolveTimeoutMilliseconds: 60_000,
                round.Token);
            round.Cancel();

            Assert.That(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        }

        /// <summary>A lookup that succeeds with nothing in it leaves no address to echo either.</summary>
        [Test]
        public async Task SendPing_AnEmptyResolutionIsUnresolved()
        {
            var resolver = new FakeResolver { ["empty.example"] = [] };
            var echo = new FakeEcho();
            var sender = CreateSender(resolver, echo);

            var result = await sender.SendPingAsync("empty.example", ReplyTimeout, ResolveTimeout);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(PingResult.Unresolved));
                Assert.That(echo.Destinations, Is.Empty);
            });
        }

        /// <summary>An unknown name is the ordinary case: a host switched off, or a typo.</summary>
        [Test]
        public async Task SendPing_AResolverThatThrowsIsUnresolved()
        {
            var sender = CreateSender(new FakeResolver(), new FakeEcho());

            var result = await sender.SendPingAsync("nope.example", ReplyTimeout, ResolveTimeout);

            Assert.That(result, Is.EqualTo(PingResult.Unresolved));
        }

        [TestCase(IPStatus.TimedOut)]
        [TestCase(IPStatus.DestinationHostUnreachable)]
        [TestCase(IPStatus.TtlExpired)]
        public async Task SendPing_AReplyThatIsNotSuccessIsUnanswered(IPStatus status)
        {
            var resolver = new FakeResolver { ["host.example"] = [IPAddress.Loopback] };
            var echo = new FakeEcho { Reply = new IcmpReply(status, 0) };
            var sender = CreateSender(resolver, echo);

            var result = await sender.SendPingAsync("host.example", ReplyTimeout, ResolveTimeout);

            Assert.That(result, Is.EqualTo(PingResult.Unanswered));
        }

        /// <summary>
        /// The distinction the database depends on: the address resolved, so there was a host to ask
        /// and the answer never came. That is a missed ping and belongs in the history, however the
        /// echo failed — a refused socket included.
        /// </summary>
        [TestCaseSource(nameof(EchoFailures))]
        public async Task SendPing_AnEchoThatThrowsIsUnansweredRatherThanUnresolved(Exception failure)
        {
            var resolver = new FakeResolver { ["host.example"] = [IPAddress.Loopback] };
            var sender = CreateSender(resolver, new FakeEcho { Throws = failure });

            var result = await sender.SendPingAsync("host.example", ReplyTimeout, ResolveTimeout);

            Assert.That(result, Is.EqualTo(PingResult.Unanswered));
        }

        /// <summary>
        /// Every enabled host is pinged every round, so a host that is simply switched off would
        /// otherwise repeat the same warning at the configured interval for as long as it is down.
        /// </summary>
        [Test]
        public async Task SendPing_LogsAFailingAddressAtMostOncePerQuietWindow()
        {
            var clock = new StubClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var logger = new ListLogger();
            var sender = CreateSender(new FakeResolver(), new FakeEcho(), clock, logger);

            await sender.SendPingAsync("gone.example", ReplyTimeout, ResolveTimeout);
            await sender.SendPingAsync("gone.example", ReplyTimeout, ResolveTimeout);

            Assert.That(logger.Warnings, Has.Count.EqualTo(1), "the second failure is inside the quiet window");

            clock.Now = clock.Now.AddMinutes(15);
            await sender.SendPingAsync("gone.example", ReplyTimeout, ResolveTimeout);

            Assert.That(logger.Warnings, Has.Count.EqualTo(2), "the window has passed, so it may warn again");
        }

        /// <summary>Two addresses failing get a window each rather than silencing one another.</summary>
        [Test]
        public async Task SendPing_QuietensEachAddressSeparately()
        {
            var logger = new ListLogger();
            var sender = CreateSender(
                new FakeResolver(),
                new FakeEcho(),
                new StubClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                logger);

            await sender.SendPingAsync("one.example", ReplyTimeout, ResolveTimeout);
            await sender.SendPingAsync("two.example", ReplyTimeout, ResolveTimeout);

            Assert.That(logger.Warnings, Has.Count.EqualTo(2));
        }

        /// <summary>
        /// The clock, resolver and echo are optional parameters the container has to fill from their
        /// default values — nothing registers them. Getting that wrong fails only at startup, which
        /// no other test reaches.
        /// </summary>
        [Test]
        public void Constructor_IsSatisfiedByTheContainerFromTheLoggerAlone()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IPingSender, PingSender>();

            using var provider = services.BuildServiceProvider();

            Assert.That(provider.GetRequiredService<IPingSender>(), Is.InstanceOf<PingSender>());
        }

        private static IEnumerable<Exception> EchoFailures()
        {
            yield return new PingException("the echo could not be sent");
            yield return new SocketException(10013); // Access denied — an unprivileged raw socket.
        }

        private static PingSender CreateSender(
            INameResolver resolver,
            IIcmpEcho echo,
            TimeProvider? timeProvider = null,
            ILogger<PingSender>? logger = null) =>
            new(logger ?? NullLogger<PingSender>.Instance, timeProvider, resolver, echo);

        /// <summary>A clock that only has to be read: the quiet window uses no timers.</summary>
        private sealed class StubClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class FakeResolver : INameResolver
        {
            private readonly Dictionary<string, IPAddress[]> _answers = [];

            public List<string> Calls { get; } = [];

            /// <summary>Waits until cancelled, standing in for a name that never comes back.</summary>
            public bool Hang { get; init; }

            public IPAddress[] this[string host]
            {
                set => _answers[host] = value;
            }

            public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            {
                Calls.Add(host);
                if (Hang)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                // An address nobody configured is an unknown name, which is what the resolver says.
                return _answers.TryGetValue(host, out var addresses)
                    ? addresses
                    : throw new SocketException(11001); // Host not found.
            }
        }

        private sealed class FakeEcho : IIcmpEcho
        {
            public IcmpReply Reply { get; init; } = new(IPStatus.Success, 1);

            public Exception? Throws { get; init; }

            public List<IPAddress> Destinations { get; } = [];

            public Task<IcmpReply> SendAsync(
                IPAddress destination,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                Destinations.Add(destination);
                return Throws is not null
                    ? Task.FromException<IcmpReply>(Throws)
                    : Task.FromResult(Reply);
            }
        }

        private sealed class ListLogger : ILogger<PingSender>
        {
            public List<string> Warnings { get; } = [];

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
                if (logLevel is LogLevel.Warning)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
