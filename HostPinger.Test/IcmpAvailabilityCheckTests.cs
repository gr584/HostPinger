using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HostPinger.Core.Services;
using Microsoft.Extensions.Logging;

namespace HostPinger.Test
{
    /// <summary>
    /// The probe at startup that says whether this process may send ICMP at all, which otherwise
    /// looks exactly like every host being down. Each of its three verdicts is a line in the log.
    /// </summary>
    public class IcmpAvailabilityCheckTests
    {
        [Test]
        public async Task StartAsync_SaysSoWhenLoopbackAnswers()
        {
            var echo = new FakeEcho { Reply = new IcmpReply(IPStatus.Success, 0) };
            var logger = new TestLogger<IcmpAvailabilityCheck>();

            await new IcmpAvailabilityCheck(logger, echo).StartAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(echo.Asked, Is.EqualTo(new[] { IPAddress.Loopback }));
                Assert.That(logger.Entries.Single().Level, Is.EqualTo(LogLevel.Information));
            });
        }

        /// <summary>
        /// A send that is accepted and never answered is how a refused ICMP socket shows up on
        /// Linux, so it is an error, naming the status and the remedy, not a lesser warning.
        /// </summary>
        [Test]
        public async Task StartAsync_ReportsALoopbackThatDoesNotAnswerAsAnError()
        {
            var logger = new TestLogger<IcmpAvailabilityCheck>();

            await new IcmpAvailabilityCheck(logger, new FakeEcho { Reply = new IcmpReply(IPStatus.TimedOut, 0) })
                .StartAsync(CancellationToken.None);

            var entry = logger.Entries.Single();
            Assert.Multiple(() =>
            {
                Assert.That(entry.Level, Is.EqualTo(LogLevel.Error));
                Assert.That(entry.Message, Does.Contain("TimedOut").And.Contain("CAP_NET_RAW"));
            });
        }

        /// <summary>A send that throws is an error with the exception, and the service still starts.</summary>
        [TestCase(typeof(SocketException))]
        [TestCase(typeof(PingException))]
        [TestCase(typeof(PlatformNotSupportedException))]
        public async Task StartAsync_ReportsASendThatThrowsAsAnError(Type thrown)
        {
            var failure = thrown == typeof(PingException)
                ? new PingException("refused", new SocketException())
                : (Exception)Activator.CreateInstance(thrown)!;
            var logger = new TestLogger<IcmpAvailabilityCheck>();

            await new IcmpAvailabilityCheck(logger, new FakeEcho { Throws = failure }).StartAsync(CancellationToken.None);

            var entry = logger.Entries.Single();
            Assert.Multiple(() =>
            {
                Assert.That(entry.Level, Is.EqualTo(LogLevel.Error));
                Assert.That(entry.Exception, Is.SameAs(failure));
                Assert.That(entry.Message, Does.Contain("CAP_NET_RAW"));
            });
        }

        /// <summary>A start that is cancelled stays cancelled, rather than being logged as ICMP being unusable.</summary>
        [Test]
        public void StartAsync_LetsACancellationThrough()
        {
            var logger = new TestLogger<IcmpAvailabilityCheck>();
            var check = new IcmpAvailabilityCheck(logger, new FakeEcho { Throws = new OperationCanceledException() });

            Assert.That(async () => await check.StartAsync(CancellationToken.None), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(logger.Entries, Is.Empty);
        }

        private sealed class FakeEcho : IIcmpEcho
        {
            public IcmpReply Reply { get; init; }

            public Exception? Throws { get; init; }

            public List<IPAddress> Asked { get; } = [];

            public Task<IcmpReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
            {
                Asked.Add(destination);
                return Throws is null ? Task.FromResult(Reply) : Task.FromException<IcmpReply>(Throws);
            }
        }
    }
}
