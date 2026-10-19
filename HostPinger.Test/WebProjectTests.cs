using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace HostPinger.Test
{
    /// <summary>
    /// What the web project is allowed to know about. Every read and write of the database lives
    /// in HostPinger.Core, behind named methods; a page that held a context again would be back to
    /// running queries on its circuit, and to writes nothing outside a browser can test.
    /// </summary>
    public class WebProjectTests
    {
        private static readonly string[] DatabaseNamespaces =
        [
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.Sqlite",
        ];

        /// <summary>
        /// Read off the web project's compiled assembly's metadata rather than its source, so that a context
        /// reached any way at all — an injection, a local, an extension method, a type argument —
        /// counts: each leaves a reference to the type it uses.
        /// </summary>
        [Test]
        public void WebProject_ReferencesNoDatabaseType()
        {
            using var stream = File.OpenRead(typeof(Program).Assembly.Location);
            using var image = new PEReader(stream);
            var metadata = image.GetMetadataReader();

            var referenced = metadata.TypeReferences
                .Select(handle => metadata.GetTypeReference(handle))
                .Select(type => $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}")
                .ToList();
            // Program resolves the paths itself, so a scan that reads the right references finds them;
            // a scan that missed would otherwise pass the assertion below by finding nothing at all.
            Assert.That(referenced, Does.Contain(typeof(HostPinger.Core.Options.PingerPaths).FullName), "the scan reads the web project's references");

            var database = referenced
                .Where(name => DatabaseNamespaces.Any(ns => name.StartsWith(ns + ".", StringComparison.Ordinal))
                    || name == "HostPinger.Core.Data.HostPingerDbContext")
                .Distinct()
                .Order()
                .ToList();

            Assert.That(
                database,
                Is.Empty,
                "The web project reaches the database directly. Give the read or write a method in "
                + "HostPinger.Core (HostStore, DatabaseReads, HostPingerDatabase) and call that instead.");
        }
    }
}
