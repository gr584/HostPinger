using System.Data.Common;
using HostPinger.Core.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HostPinger.Core.Data
{
    /// <summary>
    /// Sets <see cref="HostPingerDatabase.JournalSizeLimitBytes"/> on every connection a context
    /// opens. The pragma belongs to the connection rather than to the file, and the -wal is cut
    /// back by whichever connection writes first after a checkpoint, which can be any of them: a
    /// ping round, a change from a page, the pruner.
    /// </summary>
    internal sealed class JournalSizeLimit : DbConnectionInterceptor
    {
        public static readonly JournalSizeLimit Instance = new();

        private static readonly string Pragma = $"PRAGMA journal_size_limit = {HostPingerDatabase.JournalSizeLimitBytes};";

        private JournalSizeLimit()
        {
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = Pragma;
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Pragma;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
