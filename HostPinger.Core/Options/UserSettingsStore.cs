using System.Globalization;
using HostPinger.Core.Data;
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
        /// The row holding the hash of the web UI password. Public for the --remove-password
        /// command in Program, which writes it without this store's services being up.
        /// </summary>
        public static readonly string PasswordHashKey =
            $"{SecurityOptions.SectionName}:{nameof(SecurityOptions.PasswordHash)}";

        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>Replaced whole on every save and never mutated, so reads take no lock.</summary>
        private Dictionary<string, string> _rows = [];

        /// <summary>
        /// Reads the table into the snapshot. Called once at startup, after the migrations have
        /// run; until then the store answers with the configured defaults alone.
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            _rows = await db.UserSettings.AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        }

        /// <summary>The pinger settings in force: the configured values, under the stored rows.</summary>
        public PingerOptions CurrentPinger
        {
            get
            {
                var defaults = pingerDefaults.CurrentValue;
                var rows = _rows;
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
        }

        /// <summary>
        /// The hash of the web UI password in force: the stored row when there is one — empty
        /// meaning a removal, which has to keep overriding a hash configured in appsettings.json
        /// or the environment — and otherwise whatever configuration says.
        /// </summary>
        public string? PasswordHash =>
            _rows.TryGetValue(PasswordHashKey, out var stored)
                ? stored
                : securityDefaults.CurrentValue.PasswordHash;

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
        /// Stages one row without saving, adding or updating as the table requires. Shared with
        /// the --remove-password command in Program, so what a removal writes cannot drift from
        /// what <see cref="SavePasswordHashAsync"/> writes; the caller saves the changes.
        /// </summary>
        public static async Task StageAsync(
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
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                foreach (var (key, value) in changes)
                {
                    await StageAsync(db, key, value, cancellationToken);
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
