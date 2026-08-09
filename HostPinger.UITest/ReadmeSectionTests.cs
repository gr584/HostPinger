using HostPinger.Documentation;

namespace HostPinger.UITest
{
    /// <summary>
    /// Guards the one-copy arrangement between the README and the About page. The page renders the
    /// marked section of the embedded README rather than restating it, and every part of that —
    /// the file being embedded at all, the markers still being present, the markdown still
    /// producing the headings the page is styled around — fails silently at runtime if it breaks:
    /// the page simply shows less than it should.
    /// </summary>
    public class ReadmeSectionTests
    {
        [Test]
        public void Html_RendersTheMarkedSection()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ReadmeSection.Html, Is.Not.Empty,
                    "the README is not embedded in the web assembly, or it is empty");
                Assert.That(ReadmeSection.Html, Is.Not.EqualTo(ReadmeSection.MissingSectionHtml),
                    $"the README no longer carries the {ReadmeSection.BeginMarker} and "
                    + $"{ReadmeSection.EndMarker} markers the About page slices it on");
                Assert.That(ReadmeSection.Html, Does.Contain("<h2"),
                    "the section renders no headings, so it is not the feature documentation");
            });
        }

        /// <summary>
        /// The markers delimit the section rather than appearing inside it, and the rest of the
        /// README — installing, building, repository layout — stays off the page.
        /// </summary>
        [Test]
        public void Html_ExcludesTheMarkersAndEverythingOutsideThem()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ReadmeSection.Html, Does.Not.Contain("BEGIN: about"));
                Assert.That(ReadmeSection.Html, Does.Not.Contain("END: about"));
                Assert.That(ReadmeSection.Html, Does.Not.Contain("Repository layout"));
            });
        }

        /// <summary>
        /// The status badges are inline HTML in the markdown, which only reaches the page because
        /// the renderer passes raw HTML through. GitHub drops the class and shows the text, so the
        /// same source reads correctly in both places.
        /// </summary>
        [Test]
        public void Html_KeepsTheInlineStatusBadges()
        {
            Assert.That(ReadmeSection.Html, Does.Contain("class=\"badge"));
        }
    }
}
