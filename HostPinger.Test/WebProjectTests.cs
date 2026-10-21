using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace HostPinger.Test
{
    /// <summary>
    /// What the web project is allowed to know about. Every read and write of the database lives
    /// in HostPinger.Core, behind named methods; a page that held a context again would be back to
    /// running queries on its circuit, and to writes nothing outside a browser can test.
    /// </summary>
    /// <remarks>
    /// The web project's build enforces most of this itself: its BannedSymbols.txt lists what it
    /// may not use, and the analyzer fails the build on any use of it. What the analyzer does not
    /// report is a type named with nothing done through it, such as an injected context that is
    /// only handed on to a method in Core. These tests read the compiled assembly against the same
    /// list, which catches that.
    /// </remarks>
    public class WebProjectTests
    {
        private static readonly BanEntry[] Banned = ReadBannedSymbols();

        /// <summary>
        /// Read off the web project's compiled assembly's metadata rather than its source, so that
        /// what the analyzer lets through still counts — a context injected and only handed on to
        /// Core, a type argument, a caught exception type — since each leaves a reference to the
        /// type it names. Only code that names no type until it runs gets past both, reflection by
        /// name or <c>dynamic</c>, and nobody writes that by accident.
        /// </summary>
        [Test]
        public void WebProject_ReferencesNoDatabaseType()
        {
            var referenced = ReadTypeNames(typeof(Program).Assembly.Location, references: true);
            // Program resolves the paths itself, so a scan that reads the right references finds them;
            // a scan that missed would otherwise pass the assertion below by finding nothing at all.
            Assert.That(referenced, Does.Contain(typeof(HostPinger.Core.Options.PingerPaths).FullName), "the scan reads the web project's references");
            // Likewise a list that read as empty would ban nothing.
            Assert.That(Banned, Does.Contain(new BanEntry('N', "Microsoft.EntityFrameworkCore")), "the list is read from BannedSymbols.txt");

            var database = referenced
                .Where(name => Banned.Any(entry => entry.Covers(name)))
                .Distinct()
                .Order()
                .ToList();

            Assert.That(
                database,
                Is.Empty,
                "The web project reaches the database directly. Give the read or write a method in "
                + "HostPinger.Core (HostStore, DatabaseReads, HostPingerDatabase) and call that instead.");
        }

        /// <summary>
        /// An entry naming nothing bans nothing, and neither the analyzer nor the scan above says
        /// so: renaming the context would leave its old name on the list and the new one free to
        /// use. Checked against every assembly the runtime will load for the tests, which takes in
        /// the web project, the packages it is built against and the shared frameworks, so an entry
        /// for a framework type passes as it does in the analyzer.
        /// </summary>
        [Test]
        public void BannedSymbols_EachEntryNamesSomething()
        {
            var defined = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(path => ReadTypeNames(path, references: false))
                .ToHashSet();

            var namingNothing = Banned
                .Where(entry => !defined.Any(entry.Covers))
                .Select(entry => $"{entry.Kind}:{entry.Name}")
                .ToList();

            Assert.That(namingNothing, Is.Empty, "BannedSymbols.txt lists something none of the assemblies beside the tests declare");
        }

        /// <summary>
        /// The namespace (N:) and type (T:) entries of the web project's BannedSymbols.txt, copied
        /// beside the tests. A type reference can only be held against those; an entry of any other
        /// kind is a member, which the analyzer alone checks.
        /// </summary>
        private static BanEntry[] ReadBannedSymbols() =>
            File.ReadLines(Path.Combine(AppContext.BaseDirectory, "BannedSymbols.txt"))
                .Select(line => line.Split("//")[0].Split(';')[0].Trim())
                .Where(id => id.StartsWith("N:", StringComparison.Ordinal) || id.StartsWith("T:", StringComparison.Ordinal))
                .Select(id => new BanEntry(id[0], id[2..]))
                .ToArray();

        /// <summary>
        /// The full names of the types an assembly references, or of the ones it declares.
        /// </summary>
        private static List<string> ReadTypeNames(string path, bool references)
        {
            using var stream = File.OpenRead(path);
            using var image = new PEReader(stream);
            if (!image.HasMetadata)
            {
                return [];
            }

            var metadata = image.GetMetadataReader();
            return references
                ? metadata.TypeReferences
                    .Select(handle => metadata.GetTypeReference(handle))
                    .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
                    .ToList()
                : metadata.TypeDefinitions
                    .Select(handle => metadata.GetTypeDefinition(handle))
                    .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
                    .ToList();
        }

        private sealed record BanEntry(char Kind, string Name)
        {
            /// <summary>
            /// Whether a type's full name falls under this entry: as the type itself, or as any type
            /// in the namespace or one below it.
            /// </summary>
            public bool Covers(string typeName) => Kind == 'N'
                ? typeName.StartsWith(Name + ".", StringComparison.Ordinal)
                : typeName == Name;
        }
    }
}
