using System.Net;
using HostPinger.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HostPinger.Test
{
    /// <summary>
    /// Which names in a Host header the service answers to, and what a request under any other is
    /// given instead. The names it must refuse are the ones a website can hold in public DNS and
    /// point at the service's address.
    /// </summary>
    public class HostNameFilterTests
    {
        private static readonly string[] NoNames = [];

        /// <summary>An address is not a name anybody can register.</summary>
        [TestCase("127.0.0.1")]
        [TestCase("192.168.1.20")]
        [TestCase("10.0.0.7")]
        [TestCase("[::1]")]
        [TestCase("[fe80::1ff:fe23:4567:890a]")]
        [TestCase("[2001:db8::20]")]
        public void Allows_AnAddress(string host)
        {
            Assert.That(Default().Allows(host), Is.True);
        }

        /// <summary>
        /// A name without a dot, which the local network completes, and a name under a suffix
        /// public DNS does not delegate, whatever its case and with or without the closing dot.
        /// </summary>
        [TestCase("localhost")]
        [TestCase("LOCALHOST")]
        [TestCase("pinger.localhost")]
        [TestCase("monitor")]
        [TestCase("Monitor")]
        [TestCase("monitor.")]
        [TestCase("monitor.local")]
        [TestCase("Monitor.Local")]
        [TestCase("monitor.local.")]
        [TestCase("monitor.lan")]
        [TestCase("monitor.home.arpa")]
        [TestCase("monitor.rack2.internal")]
        [TestCase("monitor.localdomain")]
        [TestCase("monitor.home")]
        [TestCase("monitor.corp")]
        [TestCase("monitor.test")]
        public void Allows_ANameOnlyALocalNetworkGives(string host)
        {
            Assert.That(Default().Allows(host), Is.True);
        }

        /// <summary>
        /// A name in public DNS, including the ones made to look local: a local word or an address
        /// as a label of somebody's domain, or a local suffix without the dot before it.
        /// </summary>
        [TestCase("attacker.example")]
        [TestCase("attacker.com")]
        [TestCase("monitor.example.com")]
        [TestCase("local.attacker.com")]
        [TestCase("monitor.local.attacker.com")]
        [TestCase("localhost.attacker.com")]
        [TestCase("192.168.1.20.attacker.com")]
        [TestCase("127.0.0.1.nip.io")]
        [TestCase("attackerlocal.com")]
        [TestCase("attacker.notlocal")]
        [TestCase("monitor.locale")]
        [TestCase("monitor.homearpa")]
        [TestCase("monitor.arpa")]
        [TestCase("home.arpa.attacker.com")]
        [TestCase("attacker.com.")]
        public void Refuses_ANameSomebodyElseCanHold(string host)
        {
            Assert.That(Default().Allows(host), Is.False);
        }

        /// <summary>
        /// A request that names nothing is not from a browser, so there is no page behind it to
        /// stop.
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        public void Allows_ARequestThatNamesNothing(string? host)
        {
            Assert.That(Default().Allows(host), Is.True);
        }

        /// <summary>What the machine is called is answered to, and nothing else in its domain is.</summary>
        [Test]
        public void Allows_WhatThisMachineIsCalled()
        {
            var filter = new HostNameFilter(NoNames, ["Monitor.Corp.Example.com", null, ""]);

            Assert.Multiple(() =>
            {
                Assert.That(filter.Allows("monitor.corp.example.com"), Is.True);
                Assert.That(filter.Allows("MONITOR.corp.example.com."), Is.True, "case and the closing dot do not matter");
                Assert.That(filter.Allows("other.corp.example.com"), Is.False);
                Assert.That(filter.Allows("corp.example.com"), Is.False);
                Assert.That(filter.OwnNames, Is.EqualTo(new[] { "monitor.corp.example.com" }));
            });
        }

        /// <summary>
        /// The listed names: each exactly, a starred one for every name under it and not for the
        /// name itself, separated by semicolons or commas, with whatever space and case.
        /// </summary>
        [Test]
        public void Allows_TheNamesListed()
        {
            var filter = new HostNameFilter([" Pinger.Example.com ; *.intra.example.org, second.example.net.", null], NoNames);

            Assert.Multiple(() =>
            {
                Assert.That(filter.Allows("pinger.example.com"), Is.True);
                Assert.That(filter.Allows("second.example.net"), Is.True);
                Assert.That(filter.Allows("a.intra.example.org"), Is.True);
                Assert.That(filter.Allows("a.b.intra.example.org"), Is.True);
                Assert.That(filter.Allows("intra.example.org"), Is.False, "the star stands for at least one label");
                Assert.That(filter.Allows("notintra.example.org"), Is.False);
                Assert.That(filter.Allows("a.intra.example.org.attacker.com"), Is.False);
                Assert.That(filter.Allows("example.com"), Is.False);
                Assert.That(filter.Allows("x.pinger.example.com"), Is.False);
                Assert.That(filter.AnswersToAnyName, Is.False);
                Assert.That(
                    filter.Configured,
                    Is.EqualTo(new[] { "pinger.example.com", "*.intra.example.org", "second.example.net" }));
            });
        }

        /// <summary>A lone star turns the filter off, which is the way back to how it was.</summary>
        [Test]
        public void Allows_EveryNameWhenAStarIsListed()
        {
            var filter = new HostNameFilter(["pinger.example.com;*"], NoNames);

            Assert.Multiple(() =>
            {
                Assert.That(filter.AnswersToAnyName, Is.True);
                Assert.That(filter.Allows("attacker.example"), Is.True);
            });
        }

        /// <summary>A star with nothing under it is not the lone star, and matches nothing.</summary>
        [Test]
        public void Refuses_EveryOtherNameWhenTheStarHasOnlyADot()
        {
            var filter = new HostNameFilter(["*."], NoNames);

            Assert.Multiple(() =>
            {
                Assert.That(filter.AnswersToAnyName, Is.False);
                Assert.That(filter.Allows("attacker.example"), Is.False);
            });
        }

        /// <summary>
        /// The configuration is read as one value, which is what an environment variable is, and
        /// as a list, which is what somebody writing appsettings.json would write.
        /// </summary>
        [Test]
        public void FromConfiguration_ReadsOneValueOrAList()
        {
            var asValue = HostNameFilter.FromConfiguration(Configuration(
                (HostNameFilter.ConfigurationKey, "pinger.example.com;second.example.net")));
            var asList = HostNameFilter.FromConfiguration(Configuration(
                ($"{HostNameFilter.ConfigurationKey}:0", "pinger.example.com"),
                ($"{HostNameFilter.ConfigurationKey}:1", "second.example.net")));
            var unset = HostNameFilter.FromConfiguration(Configuration());

            Assert.Multiple(() =>
            {
                foreach (var filter in new[] { asValue, asList })
                {
                    Assert.That(filter.Allows("pinger.example.com"), Is.True);
                    Assert.That(filter.Allows("second.example.net"), Is.True);
                    Assert.That(filter.Allows("attacker.example"), Is.False);
                }

                Assert.That(unset.Allows("pinger.example.com"), Is.False);
                Assert.That(unset.Configured, Is.Empty);
                Assert.That(unset.AnswersToAnyName, Is.False);
            });
        }

        /// <summary>
        /// The variable's name as the environment spells it is the configuration key, so that what
        /// a refusal tells somebody to set is what is read.
        /// </summary>
        [Test]
        public void EnvironmentVariable_IsTheConfigurationKey()
        {
            Assert.That(
                HostNameFilter.EnvironmentVariable.Replace("__", ":", StringComparison.Ordinal),
                Is.EqualTo(HostNameFilter.ConfigurationKey));
        }

        /// <summary>This machine's own name is one of the names it answers to.</summary>
        [Test]
        public void FromConfiguration_AnswersToThisMachinesName()
        {
            var filter = HostNameFilter.FromConfiguration(Configuration());

            Assert.Multiple(() =>
            {
                Assert.That(filter.OwnNames, Does.Contain(Environment.MachineName.ToLowerInvariant()));
                Assert.That(filter.OwnNames, Does.Contain(Dns.GetHostName().ToLowerInvariant()));
            });
        }

        /// <summary>A name shown or logged is cut to a name's length and to what names are made of.</summary>
        [Test]
        public void Printable_KeepsOnlyWhatANameIsMadeOf()
        {
            Assert.Multiple(() =>
            {
                Assert.That(HostNameFilter.Printable("Attacker.example"), Is.EqualTo("Attacker.example"));
                Assert.That(HostNameFilter.Printable("[::1]"), Is.EqualTo("[::1]"));
                Assert.That(HostNameFilter.Printable("a\r\nb<c>\"d é"), Is.EqualTo("a??b?c??d??"));
                Assert.That(HostNameFilter.Printable(new string('a', 300)), Has.Length.EqualTo(255));
                Assert.That(HostNameFilter.Printable(null), Is.Empty);
            });
        }

        /// <summary>
        /// A request under a refused name gets a 400 that says which name and which setting, in
        /// plain text, and goes no further into the application.
        /// </summary>
        [Test]
        public async Task Middleware_RefusesWithTheNameAndTheSetting()
        {
            var wentOn = false;
            var logger = new TestLogger<HostNameFilterMiddleware>();
            var middleware = new HostNameFilterMiddleware(_ => { wentOn = true; return Task.CompletedTask; }, Default(), logger);
            var context = Request("attacker.example", 8080);

            await middleware.InvokeAsync(context);

            var body = await BodyAsync(context);
            Assert.Multiple(() =>
            {
                Assert.That(wentOn, Is.False);
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
                Assert.That(context.Response.ContentType, Does.StartWith("text/plain"));
                Assert.That(context.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
                Assert.That(body, Does.Contain("\"attacker.example\""));
                Assert.That(body, Does.Contain(HostNameFilter.EnvironmentVariable));
                Assert.That(body, Does.Not.Contain("8080"), "the port is no part of the name to list");
            });
        }

        /// <summary>A request under a name that is answered to goes on untouched.</summary>
        [TestCase("192.168.1.20")]
        [TestCase("monitor.local")]
        [TestCase("")]
        public async Task Middleware_LetsAnAnsweredNameThrough(string host)
        {
            var wentOn = false;
            var logger = new TestLogger<HostNameFilterMiddleware>();
            var middleware = new HostNameFilterMiddleware(_ => { wentOn = true; return Task.CompletedTask; }, Default(), logger);
            var context = Request(host, 8080);

            await middleware.InvokeAsync(context);

            Assert.Multiple(() =>
            {
                Assert.That(wentOn, Is.True);
                Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
                Assert.That(logger.Entries, Is.Empty);
            });
        }

        /// <summary>
        /// A refused name is logged once, with the address it came from and the setting, however
        /// often it is asked for and in whatever case.
        /// </summary>
        [Test]
        public async Task Middleware_LogsARefusedNameOnce()
        {
            var logger = new TestLogger<HostNameFilterMiddleware>();
            var middleware = new HostNameFilterMiddleware(_ => Task.CompletedTask, Default(), logger);

            await middleware.InvokeAsync(Request("pinger.example.com", 8080));
            await middleware.InvokeAsync(Request("PINGER.example.com", 8080));
            await middleware.InvokeAsync(Request("other.example.com", 8080));

            var entries = logger.Entries;
            Assert.Multiple(() =>
            {
                Assert.That(entries, Has.Count.EqualTo(2));
                Assert.That(entries.Select(e => e.Level), Is.All.EqualTo(LogLevel.Warning));
                Assert.That(entries[0].Message, Does.Contain("pinger.example.com"));
                Assert.That(entries[0].Message, Does.Contain("192.168.1.77"));
                Assert.That(entries[0].Message, Does.Contain(HostNameFilter.EnvironmentVariable));
                Assert.That(entries[1].Message, Does.Contain("other.example.com"));
            });
        }

        /// <summary>
        /// Requests under ever-changing names stop being logged at the cap, and go on being
        /// refused.
        /// </summary>
        [Test]
        public async Task Middleware_StopsLoggingNewNamesAtTheCap()
        {
            var logger = new TestLogger<HostNameFilterMiddleware>();
            var middleware = new HostNameFilterMiddleware(_ => Task.CompletedTask, Default(), logger);

            HttpContext last = null!;
            for (var i = 0; i < HostNameFilterMiddleware.MaximumLogged + 10; i++)
            {
                last = Request($"name{i}.attacker.example", 8080);
                await middleware.InvokeAsync(last);
            }

            Assert.Multiple(() =>
            {
                Assert.That(logger.Entries, Has.Count.EqualTo(HostNameFilterMiddleware.MaximumLogged));
                Assert.That(last.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            });
        }

        private static HostNameFilter Default() => new(NoNames, NoNames);

        private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();

        private static DefaultHttpContext Request(string host, int port)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = host.Length == 0 ? default : new HostString(host, port);
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.77");
            context.Response.Body = new MemoryStream();
            return context;
        }

        private static async Task<string> BodyAsync(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return await reader.ReadToEndAsync();
        }
    }
}
