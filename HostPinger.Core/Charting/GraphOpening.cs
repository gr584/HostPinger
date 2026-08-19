using System.Globalization;

namespace HostPinger.Core.Charting
{
    /// <summary>
    /// What an address asks the graph to open on, and the form it travels in one. The chart has two
    /// states and this has a case for each: a <see cref="Period"/> is a fixed stretch of the past,
    /// which it holds still on the way it does on one dragged out of it, and a
    /// <see cref="Window"/> is a width ending at now, which it goes on following the clock over.
    /// </summary>
    public abstract record GraphOpening
    {
        /// <summary>
        /// The address's names for the two ends of a period, and for the width of a window. Held
        /// here rather than on the page that reads them because the page that writes them is a
        /// different one, and a link only works while the two agree.
        /// </summary>
        public const string StartQuery = "from";

        /// <summary>The far end of a period; see <see cref="StartQuery"/>.</summary>
        public const string EndQuery = "to";

        /// <summary>The width of a window, in whole seconds; see <see cref="StartQuery"/>.</summary>
        public const string WindowQuery = "window";

        /// <summary>
        /// How each end of a period is written: UTC, to the second, in the shape ISO 8601 gives it.
        /// Absolute rather than relative so that a link goes on meaning the same stretch of time
        /// however long it is kept, and UTC so that it means the same one wherever it is opened.
        /// Seconds are as fine as it gets — what a link carries is a window on a chart, not a
        /// measurement — and every character it can produce is one a query string carries as it
        /// stands, so the address stays readable.
        /// </summary>
        private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        /// <summary>
        /// The narrowest opening this will build. A stretch of a few seconds scaled up is still a
        /// few seconds, which is a window with barely a ping in it; this is the point below which
        /// the reader is better served by a little more time than by the exact proportions. It also
        /// covers a stretch that measures as nothing, or as less than nothing where a clock stepped
        /// backwards over it, which would otherwise ask the chart to draw a range with no width.
        /// </summary>
        private static readonly TimeSpan MinimumWidth = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The widest. Past a year the chart is a line with a year of history averaged into it,
        /// which is not something anyone is reading; the cap is also what keeps "now minus the
        /// width" an instant the calendar has, whatever an address asks for.
        /// </summary>
        private static readonly TimeSpan MaximumWidth = TimeSpan.FromDays(365);

        /// <summary>Closes the hierarchy to the two cases below.</summary>
        private GraphOpening()
        {
        }

        /// <summary>The opening as a query string, ready to hang off a graph address.</summary>
        public abstract string QueryString { get; }

        /// <summary>
        /// A period twice as wide as the stretch handed to it, with that stretch in the middle: it
        /// fills the middle half of the chart, and a quarter of the width stands either side of it
        /// as what led up to it and what followed. For a stretch that is over, and so has both ends
        /// to be centred between.
        /// </summary>
        /// <param name="startUtc">The start of the stretch to put in the middle.</param>
        /// <param name="endUtc">Its end.</param>
        public static Period Around(DateTime startUtc, DateTime endUtc)
        {
            var width = endUtc - startUtc;
            var context = width > TimeSpan.Zero ? width / 2 : TimeSpan.Zero;
            var rangeStart = startUtc - context;
            var rangeEnd = endUtc + context;

            var shortfall = MinimumWidth - (rangeEnd - rangeStart);
            if (shortfall > TimeSpan.Zero)
            {
                rangeStart -= shortfall / 2;
                rangeEnd += shortfall / 2;
            }

            return new Period(rangeStart, rangeEnd);
        }

        /// <summary>
        /// A window one and a half times as wide as a stretch that is still running, so that the
        /// stretch fills the last two thirds of the chart and what led up to it the first third.
        /// The chart follows the clock over it rather than holding still: a stretch that is still
        /// running has no end to centre on, and freezing the chart on something that is still
        /// happening would leave the reader watching a picture of it rather than it. What that
        /// costs is that the proportions above are only true as it opens — the width stays put
        /// while the stretch goes on growing into it — which is the point of showing it live.
        /// </summary>
        /// <param name="stretch">How long the stretch has been running.</param>
        public static Window Following(TimeSpan stretch) => new(Clamp(stretch + stretch / 2));

        /// <summary>
        /// Reads an opening back out of an address, preferring a period where one is spelled out.
        /// Total on purpose, like <see cref="Data.HostSort.Parse"/> — a hand-typed or truncated
        /// query string leaves the reader looking at a chart rather than at an error page — so
        /// anything that is neither two readable instants in order nor a width the chart will draw
        /// comes back null, and the caller opens on whatever it would have opened on anyway.
        /// </summary>
        /// <param name="start">The <c>from</c> parameter.</param>
        /// <param name="end">The <c>to</c> parameter.</param>
        /// <param name="window">The <c>window</c> parameter.</param>
        public static GraphOpening? Parse(string? start, string? end, string? window)
        {
            if (TryParseInstant(start, out var startUtc)
                && TryParseInstant(end, out var endUtc)
                && endUtc > startUtc)
            {
                return new Period(startUtc, endUtc);
            }

            // Whole seconds and nothing else: a sign or a decimal point is not a width, and a
            // number of them past the cap above is not one this draws.
            return long.TryParse(window, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0
                && seconds <= (long)MaximumWidth.TotalSeconds
                    ? new Window(Clamp(TimeSpan.FromSeconds(seconds)))
                    : null;
        }

        private static TimeSpan Clamp(TimeSpan width) =>
            TimeSpan.FromTicks(Math.Clamp(width.Ticks, MinimumWidth.Ticks, MaximumWidth.Ticks));

        private static string Format(DateTime instantUtc) =>
            instantUtc.ToString(InstantFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// One end of a period, as UTC. An address written by <see cref="Period.QueryString"/> says
        /// so with its Z, and one typed by hand may not say anything at all; a time with no zone on
        /// it is read as UTC rather than as the server's local time, since UTC is the only reading
        /// that does not depend on which machine the link is opened against.
        /// </summary>
        private static bool TryParseInstant(string? value, out DateTime instantUtc) =>
            DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out instantUtc);

        /// <summary>
        /// A fixed stretch of the past. Two absolute instants rather than a width, because the
        /// point of one is that it stays where it was put while the clock moves on.
        /// </summary>
        /// <param name="StartUtc">The oldest instant on the chart.</param>
        /// <param name="EndUtc">The newest.</param>
        public sealed record Period(DateTime StartUtc, DateTime EndUtc) : GraphOpening
        {
            /// <inheritdoc />
            public override string QueryString =>
                $"{StartQuery}={Format(StartUtc)}&{EndQuery}={Format(EndUtc)}";
        }

        /// <summary>
        /// A width ending at now. The width is all the address fixes: what is inside it moves on
        /// with the clock, which is what makes it worth opening on something that is still going.
        /// </summary>
        /// <param name="Width">How much time the chart covers.</param>
        public sealed record Window(TimeSpan Width) : GraphOpening
        {
            /// <inheritdoc />
            public override string QueryString => $"{WindowQuery}={(long)Width.TotalSeconds}";
        }
    }
}
