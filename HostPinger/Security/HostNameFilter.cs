using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HostPinger.Security
{
    /// <summary>
    /// Decides which names in a request's Host header this service answers to, so that a website
    /// cannot have a browser on the local network drive it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it stops is DNS rebinding. A page on somebody else's site has its own name pointed at
    /// this service's address, and the browser that has the page open then treats the service as
    /// part of that site: the page reads whatever anyone on the network can read, and on an
    /// install without a password changes it too. Nobody outside has to reach the service for
    /// that — the browser inside does — so keeping the service off the internet is no defence.
    /// The one thing such a page cannot choose is the name the browser sends: it is the name the
    /// page was loaded under, and that has to be one its owner controls in public DNS.
    /// </para>
    /// <para>
    /// So the names answered to are the ones nobody can hold there: an address, a name without a
    /// dot, a name under a suffix that public DNS does not delegate, and what this machine is
    /// called. A service reached on its local network is reached by one of those. Anything else
    /// has to be listed under <see cref="ConfigurationKey"/>.
    /// </para>
    /// <para>
    /// Behind a reverse proxy this sees the name the proxy sends on, which by default is the
    /// service's own address. The proxy is then the one that has to turn unknown names away.
    /// </para>
    /// </remarks>
    internal sealed class HostNameFilter
    {
        /// <summary>
        /// Where the further names are listed: one value with the names separated by semicolons or
        /// commas, which is what fits in an environment file, or a list. <c>*.example.com</c> is
        /// every name under example.com, and a lone <c>*</c> is every name there is.
        /// </summary>
        /// <remarks>
        /// Read from the files and the environment only. The settings kept in the database cannot
        /// supply it, so restoring a backup cannot widen what the service answers to.
        /// </remarks>
        public const string ConfigurationKey = "Security:AllowedHostNames";

        /// <summary>The same key as the environment spells it, for what is said to a person.</summary>
        public const string EnvironmentVariable = "Security__AllowedHostNames";

        /// <summary>
        /// Suffixes with no delegation in public DNS, so that nobody outside can be given a name
        /// under one. localhost, local, home.arpa, internal and test are reserved for it;
        /// home and corp are the two ICANN has undertaken never to delegate, because so many
        /// networks already use them; lan and localdomain are what routers and installers hand
        /// out, and are in the root zone no more than the rest.
        /// </summary>
        private static readonly string[] LocalSuffixes =
        [
            ".localhost",
            ".local",
            ".lan",
            ".home.arpa",
            ".internal",
            ".localdomain",
            ".home",
            ".corp",
            ".test",
        ];

        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly List<string> _suffixes = [];

        /// <param name="configured">The names listed under <see cref="ConfigurationKey"/>.</param>
        /// <param name="ownNames">What this machine is called.</param>
        public HostNameFilter(IEnumerable<string?> configured, IEnumerable<string?> ownNames)
        {
            var listed = new List<string>();
            foreach (var entry in configured.SelectMany(Split))
            {
                if (entry == "*")
                {
                    AnswersToAnyName = true;
                }
                else if (entry.StartsWith("*.", StringComparison.Ordinal) && entry.Length > 2)
                {
                    _suffixes.Add(entry[1..]);
                }
                else
                {
                    _names.Add(entry);
                }

                listed.Add(entry);
            }

            Configured = listed;

            var own = new List<string>();
            foreach (var name in ownNames.Select(Normalize).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal))
            {
                _names.Add(name);
                own.Add(name);
            }

            OwnNames = own;
        }

        /// <summary>True when a lone <c>*</c> is listed, which turns the filter off.</summary>
        public bool AnswersToAnyName { get; }

        /// <summary>The names listed in the configuration, as they are matched.</summary>
        public IReadOnlyList<string> Configured { get; }

        /// <summary>What this machine is called, as it is matched.</summary>
        public IReadOnlyList<string> OwnNames { get; }

        /// <summary>The filter the running service uses: the configured names, and this machine's.</summary>
        public static HostNameFilter FromConfiguration(IConfiguration configuration)
        {
            var section = configuration.GetSection(ConfigurationKey);
            return new HostNameFilter(
                section.GetChildren().Select(child => child.Value).Prepend(section.Value),
                NamesOfThisMachine());
        }

        /// <summary>
        /// Whether a request naming <paramref name="host"/> is answered. The name is the host part
        /// of the Host header, without the port, as <see cref="HostString.Host"/> gives it.
        /// </summary>
        public bool Allows(string? host)
        {
            // Only a client that is not a browser sends no name at all, and a page in a browser
            // is the only thing this is here to stop.
            if (AnswersToAnyName || string.IsNullOrEmpty(host))
            {
                return true;
            }

            var name = Normalize(host);

            // An address is nobody's name. The brackets are how the header carries an IPv6 one.
            var address = name.StartsWith('[') && name.EndsWith(']') ? name[1..^1] : name;
            if (IPAddress.TryParse(address, out _))
            {
                return true;
            }

            // A name without a dot is completed by the network it is asked on, not by public DNS,
            // where no top-level name has an address of its own.
            if (!name.Contains('.'))
            {
                return true;
            }

            if (_names.Contains(name))
            {
                return true;
            }

            foreach (var suffix in LocalSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var suffix in _suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// What a refused request is told, written for the person who typed the name into a
        /// browser and can change the configuration.
        /// </summary>
        public static string Refusal(string? host) =>
            $"HostPinger does not answer to the name \"{Printable(host)}\".\n\n"
            + "It answers to its address, to a name without a dot, to a name under a local suffix "
            + "such as .local, .lan or .home.arpa, and to what its machine is called. That keeps a "
            + "website from reaching it through a browser on its network.\n\n"
            + $"To have it answer to this name as well, add the name to {EnvironmentVariable} in "
            + "the service's environment and restart the service. The README's \"Names the service "
            + "answers to\" has the details.\n";

        /// <summary>
        /// A name as it is safe to show and to log: what a host name can be made of, and no more of
        /// it than a name can be long.
        /// </summary>
        public static string Printable(string? host)
        {
            var name = host ?? string.Empty;
            if (name.Length > 255)
            {
                name = name[..255];
            }

            return string.Create(name.Length, name, static (span, source) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    var c = source[i];
                    span[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']' or '%' ? c : '?';
                }
            });
        }

        /// <summary>
        /// What this machine is called: its name, and that name within its domain where it has
        /// one. Nothing is looked up, so a resolver that is down, which is when a monitor is most
        /// needed, does not hold the start.
        /// </summary>
        private static IEnumerable<string?> NamesOfThisMachine()
        {
            yield return Environment.MachineName;

            string? hostName = null;
            try
            {
                hostName = Dns.GetHostName();
            }
            catch (SocketException)
            {
            }

            yield return hostName;

            string? domain = null;
            try
            {
                domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
            }

            // Where there is no domain, some systems say so in words rather than with nothing.
            if (!string.IsNullOrEmpty(hostName)
                && !hostName.Contains('.')
                && !string.IsNullOrEmpty(domain)
                && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                yield return $"{hostName}.{domain}";
            }
        }

        /// <summary>
        /// The entries of one configured value. Only a star written alone is the lone star: one
        /// that normalising would leave alone, such as <c>*.</c>, is a slip of the hand, and a slip
        /// must not be what turns the filter off.
        /// </summary>
        private static IEnumerable<string> Split(string? value)
        {
            var entries = (value ?? string.Empty)
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                var name = Normalize(entry);
                if (name.Length > 0 && (name != "*" || entry == "*"))
                {
                    yield return name;
                }
            }
        }

        /// <summary>Names match whatever their case, and with or without the dot that ends a full one.</summary>
        private static string Normalize(string? name)
        {
            var trimmed = (name ?? string.Empty).Trim().ToLowerInvariant();
            return trimmed.Length > 1 && trimmed.EndsWith('.') ? trimmed[..^1] : trimmed;
        }
    }
}
