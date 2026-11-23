namespace HostPinger.Core.Services
{
    /// <summary>Where opening the database at the start has got to; see <see cref="DatabaseOpening"/>.</summary>
    public enum DatabaseOpeningStage
    {
        /// <summary>Migrating and loading, or about to.</summary>
        Opening,

        /// <summary>
        /// A migration is pending and the disk has too little free for it; checked again every
        /// <see cref="DatabaseStartup.RoomRecheckInterval"/>.
        /// </summary>
        WaitingForRoom,

        /// <summary>The database is from a newer HostPinger, and this one will not open it.</summary>
        Refused,

        /// <summary>Migrated and loaded: the pages and the ping rounds can use it.</summary>
        Open,
    }

    /// <summary>
    /// How far the start has got opening the database. The web server comes up before the database
    /// is open, so that the pages can say what the start is waiting for. Until it is
    /// (<see cref="Opened"/>), the layout draws only the banner, in place of the page, and the
    /// ping rounds and the host summary's polling wait.
    /// </summary>
    /// <remarks>
    /// Read by the banner without reading the database: <see cref="DatabaseStartup"/> fills it in as
    /// it goes.
    /// </remarks>
    public sealed class DatabaseOpening
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DatabaseOpeningStage Stage { get; private set; } = DatabaseOpeningStage.Opening;

        /// <summary>Whether the database is open for the pages and the ping rounds.</summary>
        public bool IsOpen => Stage == DatabaseOpeningStage.Open;

        /// <summary>Completes once the database is open. Never completes for a refused one.</summary>
        public Task Opened => _opened.Task;

        /// <summary>
        /// Completes once the database is migrated and the settings and the host summary are loaded
        /// from it: <see cref="HostPingerDatabase.OpenAsync(IServiceProvider, CancellationToken)"/>
        /// says so as it returns, whoever calls it. What the host summary's polling waits for, which
        /// needs nothing else. <see cref="Opened"/> comes after it, once the start has settled the
        /// size limit too, which the ping rounds need.
        /// </summary>
        public Task Loaded => _loaded.Task;

        internal void MarkLoaded() => _loaded.TrySetResult();

        /// <summary>While waiting for room: what the migration needs free on the disk, margin included.</summary>
        public long RoomNeededBytes { get; private set; }

        /// <summary>While waiting for room: what the disk had free when it was last measured.</summary>
        public long RoomFreeBytes { get; private set; }

        /// <summary>Whether the opening is migrating the database, rather than only loading it.</summary>
        public bool IsUpgrading { get; private set; }

        /// <summary>When the migration began, while <see cref="IsUpgrading"/>.</summary>
        public DateTime? UpgradeStartedUtc { get; private set; }

        /// <summary>
        /// What the migration's journal files, the -wal and the -journal, come to now, while
        /// <see cref="IsUpgrading"/>: what it has written so far and not yet committed. Null until
        /// first measured. A measure of work done, not of how much is left: SQLite says nothing of
        /// that.
        /// </summary>
        public long? UpgradeJournalBytes { get; private set; }

        /// <summary>Why a refused database was refused, as the start logged it.</summary>
        public string? Refusal { get; private set; }

        /// <summary>
        /// The database's schema level once it is open (<see cref="HostPingerDatabase.SchemaLevel"/>),
        /// zero before then.
        /// </summary>
        public long SchemaLevel { get; private set; }

        /// <summary>
        /// Whether HostPinger 1.14.0 and earlier can no longer read the database, so that going back to
        /// one of them is not supported.
        /// </summary>
        public bool RollbackUnsupported => IsOpen && SchemaLevel >= 1;

        internal void WaitForRoom(long neededBytes, long freeBytes)
        {
            RoomNeededBytes = neededBytes;
            RoomFreeBytes = freeBytes;
            Stage = DatabaseOpeningStage.WaitingForRoom;
        }

        /// <summary>Opening, and migrating as it does when <paramref name="upgrading"/>.</summary>
        internal void Migrate(bool upgrading, DateTime nowUtc)
        {
            IsUpgrading = upgrading;
            UpgradeStartedUtc = upgrading ? nowUtc : null;
            UpgradeJournalBytes = null;
            Stage = DatabaseOpeningStage.Opening;
        }

        internal void ReportUpgrade(long journalBytes) => UpgradeJournalBytes = journalBytes;

        internal void Refuse(string reason)
        {
            Refusal = reason;
            Stage = DatabaseOpeningStage.Refused;
        }

        internal void Open(long schemaLevel)
        {
            IsUpgrading = false;
            SchemaLevel = schemaLevel;
            Stage = DatabaseOpeningStage.Open;
            _loaded.TrySetResult();
            _opened.TrySetResult();
        }
    }
}
