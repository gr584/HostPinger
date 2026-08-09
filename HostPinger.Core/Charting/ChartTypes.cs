namespace HostPinger.Core.Charting
{
    /// <summary>A point in chart pixel space.</summary>
    public readonly record struct ChartPoint(double X, double Y);

    /// <summary>A single ping observation; a null RoundtripMs means the host was down.</summary>
    public readonly record struct ChartSample(DateTime TimestampUtc, int? RoundtripMs);

    /// <summary>One host's samples prepared for charting.</summary>
    public sealed record ChartSeriesData(string Label, string Color, IReadOnlyList<ChartSample> Samples);

    /// <summary>A time range dragged out across the plot.</summary>
    /// <param name="ReachesRangeEnd">
    /// The drag ran into the right-hand edge of what was on screen. This is what separates "the
    /// last ten minutes" from "the ten minutes that happen to be the most recent ones": both cover
    /// the same span now, but only the first still means anything a minute from now, so only the
    /// first should go on following the clock.
    /// </param>
    public sealed record ChartSelection(DateTime StartUtc, DateTime EndUtc, bool ReachesRangeEnd);

    /// <summary>
    /// A series mapped to pixel space: polyline segments (split where the host was down) and
    /// markers along the baseline for failed pings.
    /// </summary>
    public sealed record ChartSeriesGeometry(
        IReadOnlyList<IReadOnlyList<ChartPoint>> Segments,
        IReadOnlyList<ChartPoint> DownMarkers);
}
