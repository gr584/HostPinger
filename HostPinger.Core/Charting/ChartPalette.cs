namespace HostPinger.Core.Charting
{
    /// <summary>
    /// Categorical series colors (validated palette). The slot order is a CVD-safety mechanism —
    /// assign slots to hosts in a stable order and never re-assign when the selection changes.
    /// </summary>
    public static class ChartPalette
    {
        public static readonly IReadOnlyList<string> SeriesColors =
        [
            "#2a78d6", // blue
            "#008300", // green
            "#e87ba4", // magenta
            "#eda100", // yellow
            "#1baf7a", // aqua
            "#eb6834", // orange
            "#4a3aa7", // violet
            "#e34948", // red
        ];

        /// <summary>Charts cap out at one series per palette slot; fold or filter beyond this.</summary>
        public static int MaxSeries => SeriesColors.Count;

        public static string GetColor(int slot)
        {
            var count = SeriesColors.Count;
            return SeriesColors[((slot % count) + count) % count];
        }
    }
}
