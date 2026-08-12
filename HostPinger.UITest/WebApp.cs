using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HostPinger.Core.Options;

namespace HostPinger.UITest
{
    /// <summary>
    /// A real instance of the application, started as its own process for the browser to drive.
    /// </summary>
    /// <remarks>
    /// Its own process rather than one hosted inside the test run: the application serves its
    /// static assets from a manifest built alongside them, so it has to run from its own output
    /// directory to be the thing users actually get. It is given a database and a settings overlay
    /// in a temporary directory, so a test run never touches the development data or an installed
    /// service's.
    /// </remarks>
    public sealed class WebApp : IAsyncDisposable
    {
        /// <summary>The password every test unlocks with, written into the overlay before the start.</summary>
        public const string Password = "the-test-password";

        /// <summary>
        /// The framework script that starts a circuit. Nothing on any page becomes interactive
        /// without it, and the application can answer for it with an empty body rather than an
        /// error, so it is checked before a single test runs.
        /// </summary>
        private const string FrameworkScript = "_framework/blazor.web.js";

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        private readonly Process _process;
        private readonly string _dataDirectory;

        private WebApp(Process process, string dataDirectory, string databasePath, string baseUrl)
        {
            _process = process;
            _dataDirectory = dataDirectory;
            DatabasePath = databasePath;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        /// <summary>
        /// The database this instance is running on, so a test can put rows in front of a page that
        /// only reads them. Writing to it from outside is safe for the same reason two browsers on
        /// one service are: SQLite locks per write, and nothing else is writing — the ping interval
        /// below puts the next round a day away.
        /// </summary>
        public string DatabasePath { get; }

        public static async Task<WebApp> StartAsync()
        {
            var directory = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "WebAppDirectory").Value!;

            var assemblyPath = Path.Combine(directory, "HostPinger.dll");
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    $"The application has not been built at {directory}. Build the solution rather "
                    + "than this project alone.", assemblyPath);
            }

            var dataDirectory = Path.Combine(Path.GetTempPath(), $"hostpinger-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var settingsPath = Path.Combine(dataDirectory, PingerPaths.SettingsFileName);
            WriteSettings(settingsPath);

            var databasePath = Path.Combine(dataDirectory, PingerPaths.DatabaseFileName);
            var baseUrl = $"http://127.0.0.1:{FreePort()}";
            var start = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { assemblyPath, "--urls", baseUrl },
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Development, and it has to be. This is a build rather than a publish, so its static
            // assets — including the framework script that starts the circuit — are resolved
            // through the manifest the SDK writes for a development run. Started as anything else
            // the application answers for that script with an empty two hundred, nothing becomes
            // interactive, and every click lands on a page with no handlers to hear it.
            // CheckItServesTheFrameworkScriptAsync is what keeps that from being silent.
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

            // The development settings name a database inside the checkout; these are read after
            // them and are what keep the run in its own temporary directory.
            start.Environment["Pinger__DatabasePath"] = databasePath;
            start.Environment["Pinger__UserSettingsPath"] = settingsPath;

            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the application.");

            // Drained so a full pipe cannot block the application; kept for the failure message.
            var output = new List<string>();
            process.OutputDataReceived += (_, e) => Capture(output, e.Data);
            process.ErrorDataReceived += (_, e) => Capture(output, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var app = new WebApp(process, dataDirectory, databasePath, baseUrl);
            try
            {
                await app.WaitUntilAnsweringAsync(output);
                await app.CheckItServesTheFrameworkScriptAsync();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            return app;
        }

        /// <summary>
        /// Puts the password in place before the application reads its settings, so every test
        /// starts against one that is set without having to go through the pages that set it.
        /// </summary>
        private static void WriteSettings(string settingsPath)
        {
            var root = new JsonObject
            {
                [SecurityOptions.SectionName] = new JsonObject
                {
                    [nameof(SecurityOptions.PasswordHash)] = PasswordHash.Hash(Password),
                },
                // Long enough that no ping round runs during a test: nothing here is about pinging,
                // and a round would only add noise and load.
                [PingerOptions.SectionName] = new JsonObject
                {
                    [nameof(PingerOptions.IntervalSeconds)] = 86_400,
                },
            };

            File.WriteAllText(settingsPath, root.ToJsonString(WriteOptions));
        }

        private static void Capture(List<string> output, string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.Add(line);
            }
        }

        /// <summary>
        /// A port nothing is listening on. Asking for port zero and letting go of it leaves a gap
        /// before the application claims it, which is the usual bargain for a test that needs a
        /// port it can name in advance.
        /// </summary>
        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private async Task WaitUntilAnsweringAsync(List<string> output)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The application exited with code {_process.ExitCode}.{Environment.NewLine}{Log(output)}");
                }

                try
                {
                    using var response = await client.GetAsync($"{BaseUrl}/health");
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                }

                await Task.Delay(200);
            }

            throw new TimeoutException($"The application never answered on {BaseUrl}.{Environment.NewLine}{Log(output)}");
        }

        /// <summary>
        /// Fails the run when the application answers for the framework script with nothing.
        /// </summary>
        /// <remarks>
        /// A missing script does not look like a failure from a test: the pages still arrive, the
        /// controls are all there, and every click on them is simply ignored — so what a run
        /// reports is a dozen timeouts on assertions that name something else entirely. One
        /// request here, before any test runs, turns that into a sentence.
        /// </remarks>
        private async Task CheckItServesTheFrameworkScriptAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync($"{BaseUrl}/{FrameworkScript}");
            var length = (await response.Content.ReadAsByteArrayAsync()).Length;
            if (response.IsSuccessStatusCode && length > 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"The application answered for {FrameworkScript} with {(int)response.StatusCode} and "
                + $"{length} bytes, so nothing on a page would become interactive and every browser "
                + "test would time out on a click that did nothing. The usual cause is the "
                + "application running outside Development from a build output, where its static "
                + "assets are not resolved; the other is a solution that has not been built.");
        }

        private static string Log(List<string> output)
        {
            lock (output)
            {
                return string.Join(Environment.NewLine, output.TakeLast(40));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();

            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A test run leaving a temporary directory behind is not worth failing over.
            }
        }
    }
}
