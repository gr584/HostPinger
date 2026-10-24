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
    /// report is a type named with nothing done through it, such as an injected factory that is
    /// only handed on to a Core service. These tests read the compiled assembly against the same
    /// list, which catches that.
    /// </remarks>
    public class WebProjectTests
    {
        private static readonly BanEntry[] Banned = ReadBannedSymbols();

        /// <summary>
        /// Read off the web project's compiled assembly's metadata rather than its source, so that
        /// what the analyzer lets through still counts — a factory injected and only handed on to
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
                "The web project names a database type: a context or connection, a type argument, or a "
                + "caught exception type. Give the read or write a method in HostPinger.Core (HostStore, "
                + "DatabaseReads, UserSettingsStore, HostPingerDatabase), call that instead, and catch the "
                + "IOException it throws where a write can fail.");
        }

        /// <summary>
        /// Every assembly the web project references is one of the shared frameworks', Core, or one
        /// a package on its PermittedPackageReference list brings. The list in HostPinger.csproj
        /// keeps a new package out at restore, but not a DLL referenced by its path, a reference to
        /// another project, or a data package reached through Core's own references; each of those
        /// shows up here as an assembly nothing permits, and the ban list above would know none of
        /// its types.
        /// </summary>
        [Test]
        public void WebProject_ReferencesOnlyTheFrameworksCoreAndItsPermittedPackages()
        {
            var framework = new[] { typeof(object), typeof(Microsoft.AspNetCore.Http.HttpContext) }
                .Select(type => Path.GetDirectoryName(type.Assembly.Location)!)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll"))
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var permitted = ReadPermittedPackages()
                .Append(typeof(HostPinger.Core.Options.PingerPaths).Assembly.GetName().Name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var referenced = ReadAssemblyReferences(typeof(Program).Assembly.Location);
            // A scan that read nothing would pass by finding nothing outside the list.
            Assert.That(referenced, Does.Contain("HostPinger.Core"), "the scan reads the web project's assembly references");

            var unpermitted = referenced
                .Where(name => !framework.Contains(name) && !permitted.Contains(name))
                .Order()
                .ToList();

            Assert.That(
                unpermitted,
                Is.Empty,
                "The web project references an assembly that is neither the framework's, Core, nor one of its "
                + "PermittedPackageReference packages. A library that reads or writes the database belongs in "
                + "HostPinger.Core, behind a method the pages call; any other, reference it as a package and "
                + "add it to the list.");
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
        /// The packages HostPinger.csproj permits, read from the copy beside the tests. Each one's
        /// assembly carries its package's name; the ones that are only build tools are never
        /// referenced, and so permit nothing.
        /// </summary>
        private static IEnumerable<string> ReadPermittedPackages() =>
            System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "HostPinger.csproj.xml"))
                .Descendants("PermittedPackageReference")
                .Select(item => (string)item.Attribute("Include")!);

        private static List<string> ReadAssemblyReferences(string path)
        {
            using var stream = File.OpenRead(path);
            using var image = new PEReader(stream);
            var metadata = image.GetMetadataReader();
            return metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                .ToList();
        }

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
