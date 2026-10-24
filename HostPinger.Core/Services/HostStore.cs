using HostPinger.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// The changes made to the hosts themselves: adding one, editing one, pausing or resuming it,
    /// and deleting it. Each hands back the host summary, read after the change where a read can be
    /// made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A change ends by asking <see cref="HostSummaryCache"/> for a read and waiting for it, so the
    /// summary that comes back normally holds the change, and the table drawn from it next is the
    /// one the change was made to see. If that read fails, or a restore has the summary suspended,
    /// it is the summary in hand instead, and the change reaches the pages with the next read that
    /// works, through <see cref="HostSummaryCache.Updated"/>. The wait is answered by the summary's
    /// own loop, so where that is not running only cancelling ends it. While the Hosts page wrote
    /// for itself, the read was each of its handlers' to remember, and a handler that forgot could
    /// not be caught in a browser: the summary still caught up on its own within the second.
    /// </para>
    /// <para>
    /// What can go wrong in a way the page has words for comes back as a
    /// <see cref="HostChangeOutcome"/> rather than an exception: an address another host already
    /// has, and a host deleted while it was being edited. Anything else that stops the database
    /// taking a change — the file busy past the provider's wait, a disk that is full — comes out
    /// as an <see cref="IOException"/> saying the change was not saved, the way a failed restore
    /// does, and is logged here, since the page only shows it. The page used to read every failure
    /// of a save as the address being taken.
    /// </para>
    /// <para>
    /// A delete does not wait for a ping round, although a round reads the hosts, pings them for
    /// seconds and only then saves an attempt for each: the round asks which of them are still
    /// there as it saves, and leaves out the attempt of one deleted meanwhile, see
    /// <see cref="PingMonitorService"/>. Taking turns with the rounds through
    /// <see cref="MaintenanceGate"/> instead had a confirmed delete wait out the slowest ping of
    /// the round under way, which with a host down is the whole ping timeout, or a backup's whole
    /// snapshot, with the dialog on screen and nothing to say why; the page leaving meanwhile
    /// dropped it, and a restore finishing meanwhile had it delete by an id read from the file
    /// that restore had just retired.
    /// </para>
    /// <para>
    /// Each change but a delete runs on the thread that asks for it, for as long as its statement
    /// takes: Microsoft.Data.Sqlite does no asynchronous I/O, and a change to one row takes
    /// milliseconds. Only the wait for the summary afterwards hands the thread back. A delete can
    /// take a host's whole history with it, which is seconds, so it runs on the thread pool
    /// instead. Cancelling ends the wait for the summary; a change already made, or a delete
    /// already asked for, stays made.
    /// </para>
    /// </remarks>
    public sealed class HostStore(
        IDbContextFactory<HostPingerDbContext> dbFactory,
        HostSummaryCache summaries,
        ILogger<HostStore> logger,
        TimeProvider? timeProvider = null)
    {
        /// <summary>
        /// SQLITE_CONSTRAINT_UNIQUE. The unique index on the address is the only one the hosts have
        /// besides their key, whose violations carry a code of their own, so on a write to a host
        /// this can only be the address.
        /// </summary>
        private const int UniqueConstraintFailed = 2067;

        /// <summary>
        /// How many of a host's attempts one statement of a delete takes away. Each batch commits on
        /// its own, so a ping round that comes due waits for one batch rather than for the whole
        /// history: one statement over the 417,000 attempts of a host down for six weeks held the
        /// database for seventeen seconds on the development machine.
        /// </summary>
        internal const int DeleteBatchSize = 5000;

        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// Adds a host, its name and address stored without the blanks around them: the address is
        /// what has to be unique, and a space typed before it would otherwise make it a second one.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The name or the address is blank once trimmed, or longer than a host's can be.
        /// </exception>
        public async Task<HostChange> AddAsync(
            string name,
            string address,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            name = Trimmed(name, MonitoredHost.MaxNameLength, nameof(name));
            address = Trimmed(address, MonitoredHost.MaxAddressLength, nameof(address));
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                db.Hosts.Add(new MonitoredHost
                {
                    Name = name,
                    Address = address,
                    IsEnabled = isEnabled,
                    CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                });

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex) when (IsAddressTaken(ex))
                {
                    return Refused(HostChangeOutcome.AddressTaken);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    throw NotSaved(ex);
                }
            }

            return await SavedAsync(cancellationToken);
        }

        /// <summary>
        /// Gives a host a new name, address and enabled state, trimmed as <see cref="AddAsync"/>
        /// trims them.
        /// </summary>
        /// <remarks>
        /// One UPDATE that says how many rows it changed, rather than finding the host and then
        /// saving it, which left a host deleted between the two to fail the save as though its
        /// address were taken.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The name or the address is blank once trimmed, or longer than a host's can be.
        /// </exception>
        public async Task<HostChange> UpdateAsync(
            int id,
            string name,
            string address,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            name = Trimmed(name, MonitoredHost.MaxNameLength, nameof(name));
            address = Trimmed(address, MonitoredHost.MaxAddressLength, nameof(address));
            int updated;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                try
                {
                    updated = await db.Hosts
                        .Where(h => h.Id == id)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(h => h.Name, name)
                                .SetProperty(h => h.Address, address)
                                .SetProperty(h => h.IsEnabled, isEnabled),
                            cancellationToken);
                }
                catch (Exception ex) when (IsAddressTaken(ex))
                {
                    return Refused(HostChangeOutcome.AddressTaken);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    throw NotSaved(ex);
                }
            }

            return updated == 0
                ? Refused(HostChangeOutcome.NotFound)
                : await SavedAsync(cancellationToken);
        }

        /// <summary>
        /// Pauses a host or resumes it. A host that is not there any more is nothing to pause, and
        /// the summary handed back simply no longer lists it.
        /// </summary>
        public async Task<HostSummarySnapshot> SetEnabledAsync(
            int id,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                try
                {
                    await db.Hosts
                        .Where(h => h.Id == id)
                        .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsEnabled, isEnabled), cancellationToken);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    throw NotSaved(ex);
                }
            }

            return await summaries.RefreshAsync(cancellationToken);
        }

        /// <summary>
        /// Deletes a host and its whole ping history. A host that is not there any more is already
        /// gone. A ping round under way is not waited for; see the remarks on this class.
        /// </summary>
        /// <remarks>
        /// The host is paused first, so that no round starting meanwhile pings it, and its history
        /// then goes <see cref="DeleteBatchSize"/> attempts at a time, each batch a transaction of
        /// its own, so that ping rounds and other writes take turns with it rather than waiting out
        /// the whole history. The host row goes last, and its cascade takes whatever a round that
        /// was already out saved after the last batch. All of it runs on the thread pool, and to
        /// the end once asked for: the page's circuit stays free meanwhile, and leaving the page
        /// does not leave half a history behind. A delete that fails partway — a disk that fills —
        /// leaves the host paused with what was not yet taken, and says so.
        /// </remarks>
        public async Task<HostSummarySnapshot> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            await Task.Run(() => DeleteHostAndHistoryAsync(id), CancellationToken.None);
            return await summaries.RefreshAsync(cancellationToken);
        }

        private async Task DeleteHostAndHistoryAsync(int id)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var paused = false;
            try
            {
                if (await db.Hosts.Where(h => h.Id == id).ExecuteUpdateAsync(s => s.SetProperty(h => h.IsEnabled, false)) == 0)
                {
                    return;
                }

                paused = true;
                int deleted;
                do
                {
                    deleted = await db.Database.ExecuteSqlAsync(
                        $"DELETE FROM PingAttempts WHERE Id IN (SELECT Id FROM PingAttempts WHERE HostId = {id} LIMIT {DeleteBatchSize});");
                }
                while (deleted == DeleteBatchSize);

                await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync();
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                throw paused ? NotFinished(ex) : NotSaved(ex);
            }
        }

        /// <summary>
        /// A name or an address as a host keeps it, refused when trimming leaves nothing or more
        /// than the column is meant to hold. The Hosts page's form refuses the same before it calls;
        /// this is for whatever else does, because SQLite enforces neither, and an empty address is
        /// resolved as this machine's own name, so a host given one would read as up.
        /// </summary>
        private static string Trimmed(string value, int maxLength, string parameterName)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("Nothing is left once the blanks around it are trimmed.", parameterName);
            }

            if (trimmed.Length > maxLength)
            {
                throw new ArgumentException($"Longer than the {maxLength} characters a host's can be.", parameterName);
            }

            return trimmed;
        }

        private async Task<HostChange> SavedAsync(CancellationToken cancellationToken) =>
            new(HostChangeOutcome.Saved, await summaries.RefreshAsync(cancellationToken));

        /// <summary>Nothing was written, so the summary in hand still says everything there is.</summary>
        private HostChange Refused(HostChangeOutcome outcome) => new(outcome, summaries.Current);

        /// <summary>
        /// SaveChanges reports a constraint as a DbUpdateException around the provider's own;
        /// ExecuteUpdate lets the provider's through as it is.
        /// </summary>
        private static bool IsAddressTaken(Exception ex) =>
            (ex as SqliteException ?? ex.InnerException as SqliteException)
                is { SqliteExtendedErrorCode: UniqueConstraintFailed };

        private static bool IsStorageFailure(Exception ex) => ex is DbUpdateException or SqliteException;

        private IOException NotSaved(Exception ex)
        {
            logger.LogWarning(ex, "A change to the hosts was not saved.");
            return new IOException($"The change was not saved: {(ex.InnerException ?? ex).Message}", ex);
        }

        private IOException NotFinished(Exception ex)
        {
            logger.LogWarning(ex, "A host's delete stopped partway; the host is paused with the rest of its history.");
            return new IOException(
                $"The delete stopped partway: {(ex.InnerException ?? ex).Message} The host is paused, with what was "
                    + "left of its history; delete it again to finish.",
                ex);
        }
    }

    /// <summary>How a change to a host ended.</summary>
    public enum HostChangeOutcome
    {
        /// <summary>The change was made.</summary>
        Saved,

        /// <summary>Another host already has the address, so nothing was written.</summary>
        AddressTaken,

        /// <summary>The host being edited is not there any more, so nothing was written.</summary>
        NotFound,
    }

    /// <summary>A change to a host, and the summary to draw after it.</summary>
    /// <param name="Outcome">Whether it was made, and if not, why not.</param>
    /// <param name="Summary">
    /// When the change was made, the summary read after it, which holds it — unless that read
    /// failed or a restore had the summary suspended, when it is the summary in hand. When it was
    /// not made, the summary in hand, since nothing was written.
    /// </param>
    public sealed record HostChange(HostChangeOutcome Outcome, HostSummarySnapshot Summary);
}
