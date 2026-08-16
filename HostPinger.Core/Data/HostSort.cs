namespace HostPinger.Core.Data
{
    /// <summary>Which column the hosts are ordered by.</summary>
    public enum HostSortColumn
    {
        /// <summary>The host's name.</summary>
        Name,

        /// <summary>The address being pinged.</summary>
        Address,

        /// <summary>Where the host stands, in the order <see cref="HostStatus"/> declares it.</summary>
        Status,

        /// <summary>The round trip of the last ping, with an unanswered one ranked behind them all.</summary>
        LastPing,

        /// <summary>When the last downtime began.</summary>
        LastDowntime,
    }

    /// <summary>
    /// How the Hosts page is ordered: one column, one direction. Held as a value rather than as a
    /// pair of fields on the page so that the click, the address bar and the ordering itself all
    /// read the same thing.
    /// </summary>
    /// <param name="Column">The column the rows are ordered on.</param>
    /// <param name="IsDescending">Whether that column is read from its far end.</param>
    public sealed record HostSort(HostSortColumn Column, bool IsDescending)
    {
        /// <summary>What the page shows until a heading is clicked: by name, A to Z.</summary>
        public static readonly HostSort Default = For(HostSortColumn.Name);

        private const string Ascending = "asc";

        private const string Descending = "desc";

        /// <summary>
        /// What each column is called in the address. Spelled out rather than taken from the enum
        /// so that renaming a member cannot quietly break links people have kept.
        /// </summary>
        private static readonly Dictionary<HostSortColumn, string> QueryNames = new()
        {
            [HostSortColumn.Name] = "name",
            [HostSortColumn.Address] = "address",
            [HostSortColumn.Status] = "status",
            [HostSortColumn.LastPing] = "last-ping",
            [HostSortColumn.LastDowntime] = "last-downtime",
        };

        /// <summary>The value of the page's <c>sort</c> parameter, or null for the default order.</summary>
        /// <remarks>
        /// The default is left out of the address altogether rather than spelled out in it: the
        /// page's own link is then the bare one, and a reader who has sorted their way back to
        /// where they started is not carrying a query string that says so.
        /// </remarks>
        public string? QueryColumn => this == Default ? null : QueryNames[Column];

        /// <summary>The value of the page's <c>dir</c> parameter, or null for the default order.</summary>
        public string? QueryDirection => this == Default ? null : IsDescending ? Descending : Ascending;

        /// <summary>
        /// A column ordered the way it is first read: see <see cref="LeadsWithTheWorst"/>.
        /// </summary>
        public static HostSort For(HostSortColumn column) => new(column, LeadsWithTheWorst(column));

        /// <summary>
        /// Reads the order back out of the address. Total on purpose — a hand-typed or stale query
        /// string leaves the reader looking at a table rather than at an error page.
        /// </summary>
        /// <param name="column">The <c>sort</c> parameter, matched case-insensitively.</param>
        /// <param name="direction">
        /// The <c>dir</c> parameter. A column named without a readable direction is ordered the way
        /// clicking that heading would order it, which is what a shortened link most likely meant.
        /// </param>
        public static HostSort Parse(string? column, string? direction)
        {
            if (!TryParseColumn(column, out var parsed))
            {
                // A direction on its own has nothing to order, so it goes back to the default too.
                return Default;
            }

            if (Ascending.Equals(direction, StringComparison.OrdinalIgnoreCase))
            {
                return new HostSort(parsed, IsDescending: false);
            }

            return Descending.Equals(direction, StringComparison.OrdinalIgnoreCase)
                ? new HostSort(parsed, IsDescending: true)
                : For(parsed);
        }

        /// <summary>
        /// Where clicking a heading lands: the column already sorted on turns round, and any other
        /// arrives in the direction it leads with.
        /// </summary>
        public HostSort ClickedOn(HostSortColumn column) =>
            column == Column ? this with { IsDescending = !IsDescending } : For(column);

        /// <summary>
        /// Orders a page's worth of hosts.
        /// </summary>
        /// <remarks>
        /// In memory rather than in the query behind <see cref="HostSummary.LoadAsync"/>: three of
        /// the five columns are worked out from the attempts after they are read — the status, the
        /// downtime and what an unanswered ping counts as — so ordering on them in SQL would mean
        /// saying all of that twice, in a query whose shape is load-bearing. The page holds every
        /// host it monitors and no more, which is a list to sort rather than a table.
        /// </remarks>
        public List<HostSummary> Apply(IEnumerable<HostSummary> rows)
        {
            var ordered = Column switch
            {
                HostSortColumn.Address => By(rows, row => row.Host.Address, StringComparer.OrdinalIgnoreCase),
                HostSortColumn.Status => By(rows, row => row.Status),
                HostSortColumn.LastPing => ByOptional(rows, LastPingKey),
                HostSortColumn.LastDowntime => ByOptional(rows, row => row.LastDowntime?.StartedUtc),
                _ => By(rows, row => row.Host.Name, StringComparer.OrdinalIgnoreCase),
            };

            // Ties fall back to the name, and A to Z whichever way the column above is read: two
            // hosts that are equally down keep the order they had before anything was sorted,
            // rather than swapping places from one refresh to the next.
            return ordered.ThenBy(row => row.Host.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Which way a column is read the first time it is clicked. The two text columns read the
        /// way a list of names is expected to; the three that say how a host is doing lead with the
        /// worst of it, which is the reason to sort by them at all — down before up, no reply
        /// before a slow one, the most recent outage first.
        /// </summary>
        private static bool LeadsWithTheWorst(HostSortColumn column) =>
            column is HostSortColumn.Status or HostSortColumn.LastPing or HostSortColumn.LastDowntime;

        /// <summary>
        /// What the Last ping column is ordered on. An unanswered ping is the far end of that
        /// column rather than a gap in it: "no reply" is a worse answer than any round trip, not a
        /// missing one. A host no round has covered yet has no key at all and sorts as missing.
        /// </summary>
        private static (bool NoReply, int RoundtripMs)? LastPingKey(HostSummary row) =>
            row.Last is { } last ? (last.RoundtripMs is null, last.RoundtripMs ?? 0) : null;

        private static bool TryParseColumn(string? name, out HostSortColumn column)
        {
            foreach (var (candidate, candidateName) in QueryNames)
            {
                if (candidateName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    column = candidate;
                    return true;
                }
            }

            column = HostSortColumn.Name;
            return false;
        }

        /// <summary>Orders on a key every host has.</summary>
        private IOrderedEnumerable<HostSummary> By<TKey>(
            IEnumerable<HostSummary> rows,
            Func<HostSummary, TKey> key,
            IComparer<TKey>? comparer = null) =>
            IsDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

        /// <summary>
        /// Orders on a key a host may not have, keeping the hosts without one at the end whichever
        /// way the column is read. A column of dashes answers neither "which is worst" nor "which
        /// is best", so it is never what the reader is brought to the top of the table to see.
        /// </summary>
        private IOrderedEnumerable<HostSummary> ByOptional<TKey>(
            IEnumerable<HostSummary> rows,
            Func<HostSummary, TKey?> key)
            where TKey : struct
        {
            var present = rows.OrderBy(row => key(row) is null);
            return IsDescending ? present.ThenByDescending(key) : present.ThenBy(key);
        }
    }
}
