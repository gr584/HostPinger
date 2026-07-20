namespace HostPinger.Core.Charting
{
    /// <summary>A point in chart pixel space.</summary>
    public readonly record struct ChartPoint(double X, double Y);

    /// <summary>A single ping observation; a null RoundtripMs means the host was down.</summary>
    public readonly record struct ChartSample(DateTime TimestampUtc, int? RoundtripMs);

    /// <summary>One host's samples prepared for charting.</summary>
    public sealed record ChartSeriesData(string Label, string Color, IReadOnlyList<ChartSample> Samples);

    /// <summary>
    /// A series mapped to pixel space: polyline segments (split where the host was down) and
    /// markers along the baseline for failed pings.
    /// </summary>
    public sealed record ChartSeriesGeometry(
        IReadOnlyList<IReadOnlyList<ChartPoint>> Segments,
        IReadOnlyList<ChartPoint> DownMarkers);
}
