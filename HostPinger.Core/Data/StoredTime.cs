namespace HostPinger.Core.Data
{
    /// <summary>
    /// How a ping attempt's time is stored: whole milliseconds since the Unix epoch, an integer
    /// SQLite keeps in six bytes and its own date functions read with the 'unixepoch' modifier
    /// (<c>datetime(TimestampUtc / 1000, 'unixepoch')</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DateTime carries 100 ns, which the column does not, so storing a time cuts it to the
    /// millisecond it falls in (<see cref="Truncate"/>): rounds it down, which is also what the
    /// migration did to the times stored before, and what EF does to a DateTime handed to a query
    /// on the column.
    /// </para>
    /// <para>
    /// Rounding a bound down is exact for "at or before" and "after", and wrong by up to a
    /// millisecond for "at or after" and "before": an attempt in the millisecond a lower bound
    /// falls inside would be counted though it came before it. Such a bound goes in through
    /// <see cref="CeilingMilliseconds"/> or <see cref="Ceiling"/> instead, rounded up, which is
    /// exact for those two. The graph's bucket edges need it, since a bucket's width is whatever
    /// the range divided by the chart's width comes to, not a whole number of milliseconds.
    /// </para>
    /// </remarks>
    internal static class StoredTime
    {
        /// <summary>The stored value for <paramref name="time"/>: its millisecond, rounded down.</summary>
        public static long ToMilliseconds(DateTime time)
        {
            var ticks = time.Ticks - DateTime.UnixEpoch.Ticks;
            var milliseconds = ticks / TimeSpan.TicksPerMillisecond;

            // Division rounds towards zero, which before 1970 is up.
            return ticks % TimeSpan.TicksPerMillisecond < 0 ? milliseconds - 1 : milliseconds;
        }

        /// <summary>The time a stored value stands for, as UTC.</summary>
        public static DateTime FromMilliseconds(long milliseconds) => new(TicksOf(milliseconds), DateTimeKind.Utc);

        /// <summary><paramref name="time"/> cut to the millisecond it falls in, which is what storing it keeps.</summary>
        public static DateTime Truncate(DateTime time) => FromMilliseconds(ToMilliseconds(time));

        /// <summary>The first stored value at or after <paramref name="time"/>: its millisecond, rounded up.</summary>
        public static long CeilingMilliseconds(DateTime time)
        {
            var milliseconds = ToMilliseconds(time);
            return TicksOf(milliseconds) < time.Ticks ? milliseconds + 1 : milliseconds;
        }

        /// <summary>
        /// <paramref name="time"/> rounded up to the millisecond, for a lower bound handed to a
        /// query; see the remarks on this class.
        /// </summary>
        public static DateTime Ceiling(DateTime time) => FromMilliseconds(CeilingMilliseconds(time));

        private static long TicksOf(long milliseconds) =>
            DateTime.UnixEpoch.Ticks + (milliseconds * TimeSpan.TicksPerMillisecond);
    }
}
