using System.Reflection;
using Markdig;

namespace HostPinger.Documentation
{
    /// <summary>
    /// The part of the README that the About page shows, sliced out of the copy embedded in this
    /// assembly and rendered to HTML.
    /// </summary>
    /// <remarks>
    /// The README is the only place that text lives — the page renders it rather than restating it,
    /// so the two cannot drift apart. Everything outside the markers describes installing and
    /// building the thing, which is for someone working on the checkout rather than someone running
    /// the service, and is left out.
    /// </remarks>
    public static class ReadmeSection
    {
        public const string BeginMarker = "<!-- BEGIN: about -->";

        public const string EndMarker = "<!-- END: about -->";

        /// <summary>Matches the LogicalName the project file gives the embedded README.</summary>
        public const string ResourceName = "README.md";

        /// <summary>
        /// Stands in for the section when the markers cannot be found, which only happens if the
        /// README loses them. A test asserts that it is not what the page ends up rendering.
        /// </summary>
        public const string MissingSectionHtml =
            "<p>The bundled README is missing the section this page renders.</p>";

        /// <summary>The rendered section. Read from the assembly and converted once, on first use.</summary>
        public static string Html { get; } = Render(ReadEmbeddedReadme());

        /// <summary>
        /// The markdown is our own compiled-in text rather than anything a user supplies, so the
        /// inline HTML it carries — the status badges — is passed through rather than escaped.
        /// </summary>
        private static string Render(string markdown)
        {
            var start = markdown.IndexOf(BeginMarker, StringComparison.Ordinal);
            var end = markdown.IndexOf(EndMarker, StringComparison.Ordinal);
            if (start < 0 || end < start)
            {
                return MissingSectionHtml;
            }

            var section = markdown[(start + BeginMarker.Length)..end];
            return Markdown.ToHtml(section, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        }

        private static string ReadEmbeddedReadme()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
