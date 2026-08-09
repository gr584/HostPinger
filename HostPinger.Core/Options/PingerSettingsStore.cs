using System.Text.Json;
using System.Text.Json.Nodes;

namespace HostPinger.Core.Options
{
    /// <summary>
    /// Writes runtime-editable settings to the JSON overlay described by <see cref="PingerPaths"/>.
    /// The overlay is registered as a reload-on-change configuration source, so a save flows back
    /// through <c>IOptionsMonitor&lt;PingerOptions&gt;</c> to the running services without a restart.
    /// </summary>
    public class PingerSettingsStore(PingerPaths paths)
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        private readonly SemaphoreSlim _gate = new(1, 1);

        public string FilePath => paths.SettingsPath;

        public Task SaveAsync(PingerSettingsUpdate update, CancellationToken cancellationToken = default) =>
            UpdateAsync(root =>
            {
                if (root[PingerOptions.SectionName] is not JsonObject section)
                {
                    section = [];
                    root[PingerOptions.SectionName] = section;
                }

                if (update.IntervalSeconds is int intervalSeconds)
                {
                    section[nameof(PingerOptions.IntervalSeconds)] = intervalSeconds;
                }

                if (update.TimeoutSeconds is int timeoutSeconds)
                {
                    section[nameof(PingerOptions.TimeoutSeconds)] = timeoutSeconds;
                }

                if (update.ResolveTimeoutSeconds is int resolveTimeoutSeconds)
                {
                    section[nameof(PingerOptions.ResolveTimeoutSeconds)] = resolveTimeoutSeconds;
                }

                if (update.RetryAttempts is int retryAttempts)
                {
                    section[nameof(PingerOptions.RetryAttempts)] = retryAttempts;
                }

                if (update.MaxDatabaseSizeMb is int maxDatabaseSizeMb)
                {
                    section[nameof(PingerOptions.MaxDatabaseSizeMb)] = maxDatabaseSizeMb;
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
        /// Removing writes the key as empty rather than deleting it. The overlay is the last
        /// configuration source, so a written key is what overrides the layers underneath it: a
        /// password that came from appsettings.json or a <c>Security__PasswordHash</c> environment
        /// variable is only removed by saying so here. Deleting the key instead would take the
        /// overlay's own value away and let that one back in — the page would report a removal that
        /// left the application locked, having just signed the person out of it.
        /// </para>
        /// </remarks>
        public Task SavePasswordHashAsync(string? hash, CancellationToken cancellationToken = default) =>
            UpdateAsync(root =>
            {
                if (root[SecurityOptions.SectionName] is not JsonObject section)
                {
                    section = [];
                    root[SecurityOptions.SectionName] = section;
                }

                section[nameof(SecurityOptions.PasswordHash)] = hash ?? string.Empty;
            }, cancellationToken);

        /// <summary>
        /// Applies <paramref name="edit"/> to the whole overlay and writes it back, one save at a
        /// time. Every setting the edit does not touch survives, whether it belongs to another
        /// group on the Configuration page or to nothing this application knows about.
        /// </summary>
        private async Task UpdateAsync(Action<JsonObject> edit, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var root = await ReadRootAsync(cancellationToken);
                edit(root);
                Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
                await File.WriteAllTextAsync(paths.SettingsPath, root.ToJsonString(WriteOptions), cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Reads the overlay so unrelated keys survive a save. A missing or unreadable file starts
        /// over rather than failing: it is a cache of edits, and the defaults still come from
        /// appsettings.json.
        /// </summary>
        private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(paths.SettingsPath))
            {
                return [];
            }

            try
            {
                var json = await File.ReadAllTextAsync(paths.SettingsPath, cancellationToken);
                return JsonNode.Parse(json) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
