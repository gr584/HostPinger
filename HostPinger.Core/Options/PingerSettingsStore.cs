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

        public async Task SaveAsync(PingerSettingsUpdate update, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var root = await ReadRootAsync(cancellationToken);
                if (root[PingerOptions.SectionName] is not JsonObject section)
                {
                    section = [];
                    root[PingerOptions.SectionName] = section;
                }

                if (update.IntervalSeconds is int intervalSeconds)
                {
                    section[nameof(PingerOptions.IntervalSeconds)] = intervalSeconds;
                }

                if (update.TimeoutMilliseconds is int timeoutMilliseconds)
                {
                    section[nameof(PingerOptions.TimeoutMilliseconds)] = timeoutMilliseconds;
                }

                if (update.MaxDatabaseSizeMb is int maxDatabaseSizeMb)
                {
                    section[nameof(PingerOptions.MaxDatabaseSizeMb)] = maxDatabaseSizeMb;
                }

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
