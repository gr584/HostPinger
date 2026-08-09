using System.Text.RegularExpressions;
using HostPinger.Core.Charting;

namespace HostPinger.UITest
{
    /// <summary>
    /// Guards the arrangement between the palette and the stylesheet. A slot is only a name here —
    /// what it looks like is decided by a <c>--chart-series-N</c> property in app.css, once for the
    /// light theme and once for the dark one. Adding a slot without adding both colors leaves the
    /// series invisible on the chart rather than failing anywhere, which is exactly the kind of
    /// break that survives a review.
    /// </summary>
    public class ChartPaletteTests
    {
        private const string StylesheetFileName = "app.css";

        [Test]
        public void GetColor_NamesTheSlotsPropertyForEverySlot()
        {
            var names = Enumerable.Range(0, ChartPalette.SlotCount).Select(ChartPalette.GetColor).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(names[0], Is.EqualTo("var(--chart-series-0)"));
                Assert.That(names, Is.Unique, "two slots share a color, so two hosts would too");
            });
        }

        /// <summary>
        /// Slots come from a host's position in a list that the user adds to and removes from, so
        /// the arithmetic has to hold either side of the range rather than assuming callers stay
        /// inside it.
        /// </summary>
        [TestCase(ChartPalette.SlotCount, 0)]
        [TestCase(ChartPalette.SlotCount + 3, 3)]
        [TestCase(-1, ChartPalette.SlotCount - 1)]
        [TestCase(-ChartPalette.SlotCount, 0)]
        public void GetColor_WrapsOntoTheSlotRange(int slot, int expectedSlot)
        {
            Assert.That(ChartPalette.GetColor(slot), Is.EqualTo(ChartPalette.GetColor(expectedSlot)));
        }

        [Test]
        public void Stylesheet_DefinesEverySlotInBothThemes()
        {
            var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, StylesheetFileName));
            var dark = DeclarationBlock(css, "[data-bs-theme=\"dark\"]");
            var light = css.Replace(dark, string.Empty);

            Assert.Multiple(() =>
            {
                for (var slot = 0; slot < ChartPalette.SlotCount; slot++)
                {
                    var property = $"--chart-series-{slot}:";
                    Assert.That(light, Does.Contain(property),
                        $"{StylesheetFileName} defines no light color for palette slot {slot}");
                    Assert.That(dark, Does.Contain(property),
                        $"{StylesheetFileName} defines no dark color for palette slot {slot}");
                }
            });
        }

        /// <summary>
        /// The body of the first rule for the given selector. The stylesheet nests nothing, so the
        /// block runs to the first closing brace.
        /// </summary>
        private static string DeclarationBlock(string css, string selector)
        {
            var match = Regex.Match(css, $"{Regex.Escape(selector)}\\s*\\{{[^}}]*}}");
            Assert.That(match.Success, Is.True, $"{StylesheetFileName} has no {selector} rule");
            return match.Value;
        }
    }
}
