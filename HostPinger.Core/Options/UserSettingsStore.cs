using System.Globalization;
using HostPinger.Core.Data;
using HostPinger.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HostPinger.Core.Options
{
    /// <summary>
    /// The settings edited while the application runs, stored as rows of the UserSettings table
    /// under the keys they would have in configuration. A stored row wins; a missing one falls
    /// through to appsettings.json, the environment and the code defaults, so the table only ever
    /// holds what somebody has changed, and a default changed in a later release shows through for
    /// everything nobody has.
    /// </summary>
    /// <remarks>
    /// Reads come from an in-memory snapshot, loaded once by <see cref="LoadAsync"/> after the
    /// migrations have run and updated by every save before it returns — so the moment a save
    /// completes, this store is already telling the truth. That is what lets the pages save and
    /// redirect without waiting for anything to reload, which the JSON overlay this store replaced
    /// made them do.
    /// </remarks>
    public class UserSettingsStore(
        IDbContextFactory<HostPingerDbContext> dbFactory,
        IOptionsMonitor<PingerOptions> pingerDefaults,
        IOptionsMonitor<SecurityOptions> securityDefaults)
    {
        /// <summary>
        /// The row holding the hash of the web UI password, which <see cref="RemovePasswordAsync"/>
        /// writes without this store's services being up.
        /// </summary>
        public static readonly string PasswordHashKey =
            $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.PasswordHash)}";

        /// <summary>The row holding the size limit, which a restore lowers in a backup this disk cannot hold.</summary>
        internal static readonly string MaxDatabaseSizeMbKey = PingerKey(nameof(PingerOptions.MaxDatabaseSizeMb));

        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Replaced whole on every save and never mutated, so reads take no lock.</summary>
        private Dictionary<string, string> _rows = [];

        /// <summary>
        /// Called once a save has read what it changes, before it writes. For the tests, which have
        /// no other way to act between the two: to start a restore there.
        /// </summary>
        internal Func<Task>? AfterStage { get; init; }

        /// <summary>
        /// Reads the table into the snapshot. Called once at startup, after the migrations have
        /// run; until then the store answers with the configured defaults alone.
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            _rows = await ReadRowsAsync(db, cancellationToken);
        }

        /// <summary>
        /// The rows stored in <paramref name="db"/>, a database that is not the one loaded, for
        /// <see cref="PutInForce"/> once it is: what <see cref="LoadAsync"/> would read from it then.
        /// For a restore, which reads its backup's before the swap, so that nothing after the swap
        /// has anything left to read.
        /// </summary>
        internal static Task<Dictionary<string, string>> StoredInAsync(HostPingerDbContext db, CancellationToken cancellationToken = default) =>
            RowsInAsync(db, cancellationToken);

        /// <summary>
        /// Puts <paramref name="rows"/>, which <see cref="StoredInAsync"/> read, in force in place of
        /// the snapshot, as <see cref="LoadAsync"/> puts what it reads. The store holds them from
        /// then on, and they are never changed.
        /// </summary>
        internal void PutInForce(Dictionary<string, string> rows) => _rows = rows;

        /// <summary>The pinger settings in force: the configured values, under the stored rows.</summary>
        public PingerOptions CurrentPinger => PingerUnder(_rows);

        /// <summary>
        /// The pinger settings that the rows stored in <paramref name="db"/> would put in force,
        /// were that the database loaded: what <see cref="CurrentPinger"/> would answer then. For a
        /// restore, which asks it of a backup before putting the backup in place.
        /// </summary>
        internal async Task<PingerOptions> PingerInAsync(HostPingerDbContext db, CancellationToken cancellationToken = default) =>
            PingerUnder(await RowsInAsync(db, cancellationToken));

        /// <summary>
        /// The hash of the web UI password in force: the stored row when there is one — empty
        /// meaning a removal, which has to keep overriding a hash configured in appsettings.json
        /// or the environment — and otherwise whatever configuration says.
        /// </summary>
        public string? PasswordHash => PasswordHashUnder(_rows);

        /// <summary>
        /// The hash of the password that the settings stored in <paramref name="db"/> would put in
        /// force, were that the database loaded: what <see cref="PasswordHash"/> would answer then.
        /// For a restore, which asks it of a backup before putting the backup in place. A backup
        /// made before the settings table existed stores nothing, and puts this install's
        /// configured password in force.
        /// </summary>
        internal async Task<string?> PasswordHashInAsync(HostPingerDbContext db, CancellationToken cancellationToken = default) =>
            PasswordHashUnder(await RowsInAsync(db, cancellationToken));

        public Task SaveAsync(PingerSettingsUpdate update, CancellationToken cancellationToken = default) =>
            UpdateAsync(changes =>
            {
                if (update.IntervalSeconds is int intervalSeconds)
                {
                    changes[PingerKey(nameof(PingerOptions.IntervalSeconds))] = Text(intervalSeconds);
                }

                if (update.TimeoutSeconds is int timeoutSeconds)
                {
                    changes[PingerKey(nameof(PingerOptions.TimeoutSeconds))] = Text(timeoutSeconds);
                }

                if (update.ResolveTimeoutSeconds is int resolveTimeoutSeconds)
                {
                    changes[PingerKey(nameof(PingerOptions.ResolveTimeoutSeconds))] = Text(resolveTimeoutSeconds);
                }

                if (update.RetryAttempts is int retryAttempts)
                {
                    changes[PingerKey(nameof(PingerOptions.RetryAttempts))] = Text(retryAttempts);
                }

                if (update.MaxDatabaseSizeMb is int maxDatabaseSizeMb)
                {
                    changes[PingerKey(nameof(PingerOptions.MaxDatabaseSizeMb))] = Text(maxDatabaseSizeMb);
                }
            }, cancellationToken);

        /// <summary>
        /// Stores the hash of the web UI password, or records that there is none when
        /// <paramref name="hash"/> is null, which leaves every action unlocked again.
        /// </summary>
        /// <remarks>
        /// A method of its own rather than another <see cref="PingerSettingsUpdate"/> property,
        /// because null there means "leave this setting alone" and removing the password has to be
        /// sayable.
        /// <para>
        /// Removing writes the row as empty rather than deleting it. A stored row is what
        /// overrides configuration: a password that came from appsettings.json or a
        /// <c>Security__PasswordHash</c> environment variable is only removed by saying so here.
        /// Deleting the row instead would let that one back in — the page would report a removal
        /// that left the application locked, having just signed the person out of it.
        /// </para>
        /// </remarks>
        public Task SavePasswordHashAsync(string? hash, CancellationToken cancellationToken = default) =>
            UpdateAsync(changes => changes[PasswordHashKey] = hash ?? string.Empty, cancellationToken);

        /// <summary>
        /// What <c>HostPinger --remove-password</c> runs instead of the service: the recovery for a
        /// lost password. It writes the same "explicitly no password" row the Password page writes,
        /// so it also cancels a hash configured in appsettings.json or the environment — which is
        /// what somebody who cannot produce the password needs it to do. A service already running
        /// reads its settings from memory, so this takes effect on its next start.
        /// </summary>
        /// <remarks>
        /// Runs without the application's services, on a context of its own. The migrations run
        /// first, making it safe against a database a newer build has not opened yet — and against
        /// no database at all, where it creates one that says no password rather than failing
        /// somebody who is locked out.
        /// <para>
        /// Not for a page: it writes past the running service's settings, which go on holding the
        /// old hash until the next start. A page removes the password with
        /// <see cref="SavePasswordHashAsync"/> and null, which updates them as it writes.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Whether the migrations first had to clear a lock that a migration stopped partway through
        /// left behind, for the command to tell whoever ran it, having no log of its own.
        /// </returns>
        public static async Task<bool> RemovePasswordAsync(string databasePath, CancellationToken cancellationToken = default)
        {
            await using var db = new HostPingerDbContext(HostPingerDatabase.Options(databasePath));
            var clearedLock = await HostPingerDatabase.InitializeAsync(db, cancellationToken);
            await StageAsync(db, PasswordHashKey, string.Empty, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return clearedLock;
        }

        /// <summary>
        /// Stages one row without saving, adding or updating as the table requires. Shared with
        /// <see cref="RemovePasswordAsync"/>, so what a removal writes cannot drift from what
        /// <see cref="SavePasswordHashAsync"/> writes; the caller saves the changes.
        /// </summary>
        internal static async Task StageAsync(
            HostPingerDbContext db,
            string key,
            string value,
            CancellationToken cancellationToken = default)
        {
            var row = await db.UserSettings.FindAsync([key], cancellationToken);
            if (row is null)
            {
                db.UserSettings.Add(new UserSetting { Key = key, Value = value });
            }
            else
            {
                row.Value = value;
            }
        }

        /// <summary>
        /// Writes the edited rows to the database and then to the snapshot, one save at a time.
        /// The snapshot moves second and only if the database accepted the save, so nothing ever
        /// reads a value that was not stored.
        /// </summary>
        private async Task UpdateAsync(Action<Dictionary<string, string>> edit, CancellationToken cancellationToken)
        {
            var changes = new Dictionary<string, string>();
            edit(changes);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                // One connection from the first read to the snapshot's update below, so that a
                // restore, which replaces the snapshot once its file is in place, swaps the file
                // before the save or after it. Swapped in between, the snapshot updated here would
                // describe the file the restore replaced, its password included.
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                await db.Database.OpenConnectionAsync(cancellationToken);
                foreach (var (key, value) in changes)
                {
                    await StageAsync(db, key, value, cancellationToken);
                }

                if (AfterStage is { } afterStage)
                {
                    await afterStage();
                }

                await db.SaveChangesAsync(cancellationToken);

                var rows = new Dictionary<string, string>(_rows);
                foreach (var (key, value) in changes)
                {
                    rows[key] = value;
                }

                _rows = rows;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The table's rows, as the snapshot holds them.</summary>
        private static Task<Dictionary<string, string>> ReadRowsAsync(HostPingerDbContext db, CancellationToken cancellationToken) =>
            db.UserSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        /// <summary>
        /// The rows of a database that is not the one loaded, which may be one made before the
        /// settings table existed and so have none.
        /// </summary>
        private static async Task<Dictionary<string, string>> RowsInAsync(HostPingerDbContext db, CancellationToken cancellationToken)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UserSettings';";
                var hasTable = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
                return hasTable ? await ReadRowsAsync(db, cancellationToken) : [];
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        /// <summary>The configured pinger settings under <paramref name="rows"/>; see <see cref="CurrentPinger"/>.</summary>
        private PingerOptions PingerUnder(Dictionary<string, string> rows)
        {
            var defaults = pingerDefaults.CurrentValue;
            return new PingerOptions
            {
                DatabasePath = defaults.DatabasePath,
                IntervalSeconds = Stored(rows, nameof(PingerOptions.IntervalSeconds), defaults.IntervalSeconds),
                TimeoutSeconds = Stored(rows, nameof(PingerOptions.TimeoutSeconds), defaults.TimeoutSeconds),
                ResolveTimeoutSeconds = Stored(rows, nameof(PingerOptions.ResolveTimeoutSeconds), defaults.ResolveTimeoutSeconds),
                RetryAttempts = Stored(rows, nameof(PingerOptions.RetryAttempts), defaults.RetryAttempts),
                MaxDatabaseSizeMb = Stored(rows, nameof(PingerOptions.MaxDatabaseSizeMb), defaults.MaxDatabaseSizeMb),
            };
        }

        /// <summary>
        /// The stored password row of <paramref name="rows"/> when there is one, and otherwise
        /// whatever configuration says; see <see cref="PasswordHash"/>.
        /// </summary>
        private string? PasswordHashUnder(Dictionary<string, string> rows) =>
            rows.TryGetValue(PasswordHashKey, out var stored)
                ? stored
                : securityDefaults.CurrentValue.PasswordHash;

        /// <summary>
        /// The stored value when there is a row and it parses. A row that does not parse falls
        /// back the way a missing one does: it is one setting gone strange, not a reason the
        /// application cannot know its interval.
        /// </summary>
        private static int Stored(Dictionary<string, string> rows, string name, int fallback) =>
            rows.TryGetValue(PingerKey(name), out var value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

        private static string PingerKey(string name) => $"{PingerOptions.SectionName}:{name}";

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
