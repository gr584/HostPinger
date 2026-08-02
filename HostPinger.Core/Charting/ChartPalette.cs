namespace HostPinger.Core.Charting
{
    /// <summary>
    /// Categorical series colors (validated palette). The slot order is a CVD-safety mechanism —
    /// assign slots to hosts in a stable order and never re-assign when the selection changes.
    /// </summary>
    /// <remarks>
    /// A slot names a CSS custom property rather than carrying a color of its own. What the slot
    /// looks like is decided in <c>wwwroot/app.css</c>, which gives each one a light and a dark
    /// value — the theme is only known in the browser, so it cannot be resolved here.
    /// </remarks>
    public static class ChartPalette
    {
        /// <summary>
        /// Matches the number of <c>--chart-series-N</c> properties app.css defines, in each
        /// theme. A test guards the pair.
        /// </summary>
        public const int SlotCount = 8;

        /// <summary>Charts cap out at one series per palette slot; fold or filter beyond this.</summary>
        public static int MaxSeries => SlotCount;

        public static string GetColor(int slot) =>
            $"var(--chart-series-{((slot % SlotCount) + SlotCount) % SlotCount})";
    }
}
