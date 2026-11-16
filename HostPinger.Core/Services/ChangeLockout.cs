using Microsoft.Extensions.Logging;

namespace HostPinger.Core.Services
{
    /// <summary>
    /// Keeps every browser from changing anything while a backup or a restore is under way, and
    /// lets only one of the two be under way at a time. A backup or a restore takes the hold when
    /// its button is pressed and keeps it until it has ended; every change a page makes, to a
    /// host, a setting or the password, takes a pass for as long as it runs, and is refused while
    /// the hold is taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A restore used to ask whether the browser was unlocked, and for the current password, when
    /// it started, and replace the database minutes later without asking again. A password set or
    /// changed from another browser in between locked the one restoring, and the restore went
    /// through all the same, taking that password away with the database it was stored in. Now
    /// nothing can be changed in between: the hold is taken first, the changes already under way
    /// are waited for, and only then is anything asked, so the answers still hold at the swap.
    /// </para>
    /// <para>
    /// It also settles what a page does about a restore under its feet. A change by a host's id
    /// that arrived during a restore used to wait for the swap and then go to whichever host has
    /// that id in the restored file. Refused here, it goes nowhere; the pages close the dialogs
    /// they have open when the hold is taken, and a restore gives the hold back only once the
    /// hosts have been read from the restored file and handed to them.
    /// </para>
    /// <para>
    /// A hold is either waiting for somebody, at a password or at the choice of a file, or at
    /// work. Waiting is limited, by <see cref="WaitLimit"/> for each step, and a hold left waiting
    /// past it gives itself back: an overlay left open, or a page that went away without saying
    /// so, must not keep every browser out for good. Work is not limited, since a copy or an
    /// upload takes what it takes.
    /// </para>
    /// <para>
    /// The ping rounds and the pruner are not changes in this sense and go on throughout, as do
    /// all reads. Unlocking and locking a browser write a cookie and nothing here.
    /// </para>
    /// </remarks>
    public sealed class ChangeLockout(TimeProvider? timeProvider = null, ILogger<ChangeLockout>? logger = null)
    {
        private const string StillSaving =
            "A change was still being saved, so nothing was started. Try again in a moment.";

        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        private readonly ILogger<ChangeLockout>? _logger = logger;
        private readonly object _lock = new();

        private Hold? _hold;

        // Passes out: the changes under way, which a hold newly taken waits for.
        private int _changes;

        // Set once the changes that were under way when the hold was taken are done.
        private TaskCompletionSource? _settled;

        /// <summary>
        /// How long a hold may be left waiting for somebody at one step before it gives itself
        /// back. Settable for the tests, which would otherwise spend five minutes watching it run
        /// out.
        /// </summary>
        public TimeSpan WaitLimit { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long a hold newly taken waits for the changes under way before it is given up: the
        /// same half minute a restore's swap waits for the file. Settable for the tests.
        /// </summary>
        internal TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Which of the two is under way, and null while neither is.</summary>
        public LockoutActivity? Active
        {
            get
            {
                lock (_lock)
                {
                    return _hold?.Activity;
                }
            }
        }

        /// <summary>
        /// Raised when a hold is taken and when it is given back, on whichever thread did it, so a
        /// page hands it on to its own circuit before drawing anything.
        /// </summary>
        public event Action? Changed;

        /// <summary>What is under way, as the start of a sentence.</summary>
        public static string Describe(LockoutActivity activity) =>
            activity == LockoutActivity.Backup ? "A backup is under way" : "A restore is under way";

        /// <summary>
        /// Takes the hold for <paramref name="activity"/>, or says why not: the other, or another
        /// of the same, already has it, or a change under way did not finish in time. New changes
        /// are refused from the moment this is called, and the hold comes back once those already
        /// under way are done, waiting for somebody at its first step.
        /// </summary>
        public async Task<LockoutTake> TakeAsync(LockoutActivity activity, CancellationToken cancellationToken = default)
        {
            Hold hold;
            Task settled;
            lock (_lock)
            {
                if (_hold is { } other)
                {
                    return new LockoutTake(null, $"{Describe(other.Activity)}. Try again once it has finished.");
                }

                hold = _hold = new Hold(this, activity);
                _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_changes == 0)
                {
                    _settled.TrySetResult();
                }

                settled = _settled.Task;
            }

            RaiseChanged();
            try
            {
                await settled.WaitAsync(SettleTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                hold.Dispose();
                return new LockoutTake(null, StillSaving);
            }
            catch
            {
                hold.Dispose();
                throw;
            }

            hold.TryStartWaiting();
            return new LockoutTake(hold, null);
        }

        /// <summary>
        /// A pass for one change, held until it is disposed of, which is what a hold newly taken
        /// waits for.
        /// </summary>
        /// <exception cref="ChangeRefusedException">A backup or a restore is under way.</exception>
        internal IDisposable EnterChange()
        {
            lock (_lock)
            {
                if (_hold is { } hold)
                {
                    throw new ChangeRefusedException(
                        $"{Describe(hold.Activity)}. Nothing can be changed until it has finished.");
                }

                _changes++;
            }

            return new ChangePass(this);
        }

        private void LeaveChange()
        {
            lock (_lock)
            {
                if (--_changes == 0)
                {
                    _settled?.TrySetResult();
                }
            }
        }

        private void RaiseChanged()
        {
            if (Changed is not { } changed)
            {
                return;
            }

            // One at a time, so that a page that fails to take it cannot keep it from the rest.
            foreach (var handler in changed.GetInvocationList().Cast<Action>())
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "A page could not be told that a backup or a restore had started or ended.");
                }
            }
        }

        private sealed class ChangePass(ChangeLockout owner) : IDisposable
        {
            private int _left;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _left, 1) == 0)
                {
                    owner.LeaveChange();
                }
            }
        }

        /// <summary>
        /// A backup's or a restore's hold on the lockout, from its button to its end. Disposing of
        /// it gives it back, and so does leaving it waiting for longer than
        /// <see cref="WaitLimit"/>.
        /// </summary>
        public sealed class Hold : IDisposable
        {
            private readonly ChangeLockout _owner;
            private readonly CancellationTokenSource _ended = new();

            private ITimer? _timer;

            // Which wait the timer running was started for, so that one that fires late, after the
            // step it timed has been answered, ends nothing.
            private int _wait;
            private bool _isOver;
            private bool _ranOut;
            private DateTimeOffset? _waitingUntil;

            internal Hold(ChangeLockout owner, LockoutActivity activity)
            {
                _owner = owner;
                Activity = activity;
                Ended = _ended.Token;
            }

            /// <summary>What the hold was taken for.</summary>
            public LockoutActivity Activity { get; }

            /// <summary>Cancelled when the hold is given back, however that comes about.</summary>
            public CancellationToken Ended { get; }

            /// <summary>Whether the hold gave itself back because it was left waiting too long.</summary>
            public bool RanOut
            {
                get
                {
                    lock (_owner._lock)
                    {
                        return _ranOut;
                    }
                }
            }

            /// <summary>
            /// When the step being waited at runs out; null while the hold is at work, and once it
            /// has been given back.
            /// </summary>
            public DateTimeOffset? WaitingUntil
            {
                get
                {
                    lock (_owner._lock)
                    {
                        return _waitingUntil;
                    }
                }
            }

            /// <summary>
            /// Starts a step that waits for somebody, with the whole <see cref="WaitLimit"/> to
            /// answer it in. Called once for a step, not for each answer, so that wrong answers
            /// buy no more time.
            /// </summary>
            /// <returns>False when the hold has been given back already.</returns>
            public bool TryStartWaiting()
            {
                lock (_owner._lock)
                {
                    if (_isOver)
                    {
                        return false;
                    }

                    var wait = ++_wait;
                    _timer?.Dispose();
                    _waitingUntil = _owner._timeProvider.GetUtcNow() + _owner.WaitLimit;
                    _timer = _owner._timeProvider.CreateTimer(
                        _ => RunOut(wait), null, _owner.WaitLimit, Timeout.InfiniteTimeSpan);
                    return true;
                }
            }

            /// <summary>Starts work, which no limit runs against.</summary>
            /// <returns>False when the hold has been given back already.</returns>
            public bool TryStartWork()
            {
                lock (_owner._lock)
                {
                    if (_isOver)
                    {
                        return false;
                    }

                    _wait++;
                    _timer?.Dispose();
                    _timer = null;
                    _waitingUntil = null;
                    return true;
                }
            }

            /// <summary>Gives the hold back. Giving it back twice does nothing the second time.</summary>
            public void Dispose() => End(ranOut: false, wait: null);

            private void RunOut(int wait)
            {
                try
                {
                    End(ranOut: true, wait);
                }
                catch (Exception ex)
                {
                    // An exception out of a timer's callback would end the process.
                    _owner._logger?.LogWarning(ex, "A backup or a restore left waiting could not be let go of cleanly.");
                }
            }

            private void End(bool ranOut, int? wait)
            {
                lock (_owner._lock)
                {
                    if (_isOver || (wait is { } timed && timed != _wait))
                    {
                        return;
                    }

                    _isOver = true;
                    _ranOut = ranOut;
                    _waitingUntil = null;
                    _timer?.Dispose();
                    _timer = null;
                    if (_owner._hold == this)
                    {
                        _owner._hold = null;
                        _owner._settled = null;
                    }
                }

                // Outside the lock: both run other people's code. The lockout is open again by
                // now, so whoever hears of it can take it.
                try
                {
                    _ended.Cancel();
                }
                catch (Exception ex)
                {
                    // Whoever gives the hold back, a restore that is done among them, must not
                    // fail on what a page registered to hear of it.
                    _owner._logger?.LogWarning(ex, "A page could not be told that its backup or restore had ended.");
                }

                _owner.RaiseChanged();
            }
        }
    }

    /// <summary>What a hold on the <see cref="ChangeLockout"/> is taken for.</summary>
    public enum LockoutActivity
    {
        /// <summary>A backup: from its button until the snapshot is written, before it is downloaded.</summary>
        Backup,

        /// <summary>A restore: from its button until the backup is in place, or the restore is given up.</summary>
        Restore,
    }

    /// <summary>What came of asking for the hold.</summary>
    /// <param name="Hold">The hold, which is the caller's to give back; null when it was refused.</param>
    /// <param name="Refusal">Why it was refused, as the sentence to show whoever asked; null with a hold.</param>
    public sealed record LockoutTake(ChangeLockout.Hold? Hold, string? Refusal);

    /// <summary>
    /// A change that was not made because a backup or a restore is under way. An
    /// <see cref="IOException"/>, as every other change that was not saved is, with the sentence
    /// to show whoever asked.
    /// </summary>
    public sealed class ChangeRefusedException(string message) : IOException(message);
}
