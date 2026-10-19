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
    /// A delete takes turns with the ping rounds through <see cref="MaintenanceGate"/>. A round
    /// reads the hosts, pings them for seconds and then saves an attempt for each in one go; a host
    /// deleted in between would leave one of those attempts pointing at nothing, the foreign key
    /// would refuse the whole save, and the round would lose every host's ping.
    /// </para>
    /// <para>
    /// Each change runs on the thread that asks for it, for as long as its statement takes:
    /// Microsoft.Data.Sqlite does no asynchronous I/O. Only the wait for the summary afterwards
    /// hands the thread back. Cancelling ends that wait; a change already made stays made.
    /// </para>
    /// </remarks>
    public sealed class HostStore(
        IDbContextFactory<HostPingerDbContext> dbFactory,
        HostSummaryCache summaries,
        MaintenanceGate maintenanceGate,
        ILogger<HostStore> logger,
        TimeProvider? timeProvider = null)
    {
        /// <summary>
        /// SQLITE_CONSTRAINT_UNIQUE. The unique index on the address is the only one the hosts have
        /// besides their key, whose violations carry a code of their own, so on a write to a host
        /// this can only be the address.
        /// </summary>
        private const int UniqueConstraintFailed = 2067;

        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// Adds a host, its name and address stored without the blanks around them: the address is
        /// what has to be unique, and a space typed before it would otherwise make it a second one.
        /// </summary>
        public async Task<HostChange> AddAsync(
            string name,
            string address,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                db.Hosts.Add(new MonitoredHost
                {
                    Name = name.Trim(),
                    Address = address.Trim(),
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
        public async Task<HostChange> UpdateAsync(
            int id,
            string name,
            string address,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            name = name.Trim();
            address = address.Trim();
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
        /// Deletes a host and, through the cascade on its attempts, its whole ping history, once any
        /// ping round, backup or restore under way has finished. A host that is not there any more
        /// is already gone.
        /// </summary>
        public async Task<HostSummarySnapshot> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            using (await maintenanceGate.AcquireAsync(cancellationToken))
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                try
                {
                    await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(cancellationToken);
                }
                catch (Exception ex) when (IsStorageFailure(ex))
                {
                    throw NotSaved(ex);
                }
            }

            return await summaries.RefreshAsync(cancellationToken);
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
