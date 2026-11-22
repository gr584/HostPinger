namespace HostPinger.Core.Services
{
    /// <summary>
    /// What a database's journal files, its -wal and its -journal, come to: what a migration has
    /// written and not yet committed. SQLite says nothing of how far a migration has got, so this is
    /// what the start and a restore show of one under way, with the time it has taken.
    /// </summary>
    internal static class JournalFiles
    {
        /// <summary>How often a migration's journal files are measured.</summary>
        public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>What the journal files beside <paramref name="databasePath"/> come to now.</summary>
        public static long BytesBeside(string databasePath) =>
            SizeOf(databasePath + "-wal") + SizeOf(databasePath + "-journal");

        /// <summary>
        /// Hands <paramref name="report"/> what the journal files come to, at once and then every
        /// <paramref name="interval"/>, until <paramref name="cancellationToken"/> says the migration
        /// is over.
        /// </summary>
        public static async Task SampleAsync(
            string databasePath,
            TimeSpan interval,
            Action<long> report,
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    report(BytesBeside(databasePath));
                    await Task.Delay(interval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private static long SizeOf(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.Length : 0;
            }
            catch (IOException)
            {
                // Gone between the look and the measure, as a journal is at a commit.
                return 0;
            }
        }
    }
}
