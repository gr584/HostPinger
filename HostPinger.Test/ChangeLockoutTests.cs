using HostPinger.Core.Services;

namespace HostPinger.Test
{
    /// <summary>
    /// What keeps every browser from changing anything while a backup or a restore is under way,
    /// and lets only one of the two be under way at a time.
    /// </summary>
    public class ChangeLockoutTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The rule itself: from the moment the hold is taken, a change is refused, in the words
        /// the page shows, and once it is given back, changes go through again.
        /// </summary>
        [TestCase(LockoutActivity.Backup, "A backup is under way. Nothing can be changed until it has finished.")]
        [TestCase(LockoutActivity.Restore, "A restore is under way. Nothing can be changed until it has finished.")]
        public async Task WhileTheHoldIsTaken_AChangeIsRefused(LockoutActivity activity, string sentence)
        {
            var lockout = new ChangeLockout();
            using (lockout.EnterChange())
            {
            }

            var hold = (await lockout.TakeAsync(activity)).Hold!;
            var refused = Assert.Throws<ChangeRefusedException>(() => lockout.EnterChange());
            hold.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(refused!.Message, Is.EqualTo(sentence));
                Assert.That(lockout.Active, Is.Null);
                Assert.DoesNotThrow(() => lockout.EnterChange().Dispose());
            });
        }

        /// <summary>One at a time: whichever asks second is told which of the two has the hold.</summary>
        [TestCase(LockoutActivity.Backup, "A backup is under way. Try again once it has finished.")]
        [TestCase(LockoutActivity.Restore, "A restore is under way. Try again once it has finished.")]
        public async Task WhileTheHoldIsTaken_AnotherTakeIsTurnedAway(LockoutActivity first, string sentence)
        {
            var lockout = new ChangeLockout();
            using var hold = (await lockout.TakeAsync(first)).Hold!;

            var second = await lockout.TakeAsync(LockoutActivity.Restore);

            Assert.Multiple(() =>
            {
                Assert.That(second.Hold, Is.Null);
                Assert.That(second.Refusal, Is.EqualTo(sentence));
                Assert.That(lockout.Active, Is.EqualTo(first), "and the first still has it");
            });
        }

        /// <summary>
        /// A change under way when the hold is asked for is waited for, so that whatever is asked
        /// under the hold is asked after the last change that could alter the answer. New changes
        /// are refused from the asking on, not only once the hold is handed over: otherwise a run
        /// of them could keep it from ever being.
        /// </summary>
        [Test]
        public async Task Take_WaitsForAChangeUnderWayAndRefusesNewOnesMeanwhile()
        {
            var lockout = new ChangeLockout();
            var change = lockout.EnterChange();

            var taking = lockout.TakeAsync(LockoutActivity.Restore);
            await Task.Delay(200);
            var handedOverEarly = taking.IsCompleted;
            var activeWhileWaiting = lockout.Active;
            var refusedMeanwhile = Assert.Throws<ChangeRefusedException>(() => lockout.EnterChange());
            change.Dispose();
            using var hold = (await taking.WaitAsync(Patience)).Hold;

            Assert.Multiple(() =>
            {
                Assert.That(handedOverEarly, Is.False, "the hold waited for the change");
                Assert.That(activeWhileWaiting, Is.EqualTo(LockoutActivity.Restore), "and was already in force");
                Assert.That(refusedMeanwhile, Is.Not.Null);
                Assert.That(hold, Is.Not.Null);
            });
        }

        /// <summary>
        /// A change that never ends must not leave everything refused for good: the take gives up,
        /// says so, and the lockout is open again.
        /// </summary>
        [Test]
        public async Task Take_GivesUpWhenAChangeUnderWayOutlastsItsWait()
        {
            var lockout = new ChangeLockout { SettleTimeout = TimeSpan.FromMilliseconds(100) };
            using var change = lockout.EnterChange();

            var take = await lockout.TakeAsync(LockoutActivity.Backup).WaitAsync(Patience);

            Assert.Multiple(() =>
            {
                Assert.That(take.Hold, Is.Null);
                Assert.That(take.Refusal, Is.EqualTo("A change was still being saved, so nothing was started. Try again in a moment."));
                Assert.That(lockout.Active, Is.Null);
                Assert.DoesNotThrow(() => lockout.EnterChange().Dispose());
            });
        }

        /// <summary>A page that goes away while its take waits for a change leaves nothing taken.</summary>
        [Test]
        public async Task Take_CancelledWhileItWaitsLeavesNothingTaken()
        {
            var lockout = new ChangeLockout();
            using var change = lockout.EnterChange();
            using var leaving = new CancellationTokenSource();

            var taking = lockout.TakeAsync(LockoutActivity.Restore, leaving.Token);
            await leaving.CancelAsync();

            await Assert.CatchAsync<OperationCanceledException>(() => taking.WaitAsync(Patience));
            Assert.That(lockout.Active, Is.Null);
        }

        /// <summary>
        /// An overlay left open, or a page gone without saying so, must not keep every browser out
        /// for good: a hold left waiting gives itself back, says that is how it ended, and is of
        /// no use to whatever is handed it afterwards.
        /// </summary>
        [Test]
        public async Task Hold_LeftWaitingGivesItselfBack()
        {
            var lockout = new ChangeLockout { WaitLimit = TimeSpan.FromMilliseconds(100) };
            var hold = (await lockout.TakeAsync(LockoutActivity.Restore)).Hold!;
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = hold.Ended.Register(() => ended.TrySetResult());

            await ended.Task.WaitAsync(Patience);

            Assert.Multiple(() =>
            {
                Assert.That(hold.RanOut, Is.True);
                Assert.That(hold.WaitingUntil, Is.Null);
                Assert.That(lockout.Active, Is.Null);
                Assert.That(hold.TryStartWork(), Is.False);
                Assert.That(hold.TryStartWaiting(), Is.False);
                Assert.DoesNotThrow(() => lockout.EnterChange().Dispose());
            });
        }

        /// <summary>A copy or an upload takes what it takes, so no limit runs against a hold at work.</summary>
        [Test]
        public async Task Hold_AtWorkIsNotLimited()
        {
            var lockout = new ChangeLockout { WaitLimit = TimeSpan.FromMilliseconds(100) };
            using var hold = (await lockout.TakeAsync(LockoutActivity.Backup)).Hold!;

            var started = hold.TryStartWork();
            await Task.Delay(500);

            Assert.Multiple(() =>
            {
                Assert.That(started, Is.True);
                Assert.That(hold.WaitingUntil, Is.Null);
                Assert.That(hold.Ended.IsCancellationRequested, Is.False);
                Assert.That(hold.RanOut, Is.False);
                Assert.That(lockout.Active, Is.EqualTo(LockoutActivity.Backup));
            });
        }

        /// <summary>
        /// Each step that waits for somebody has the whole wait to itself: one started when most
        /// of the step before it had gone is not cut short by that step's timer.
        /// </summary>
        [Test]
        public async Task Hold_ANewStepHasTheWholeWait()
        {
            var lockout = new ChangeLockout { WaitLimit = TimeSpan.FromMilliseconds(1500) };
            using var hold = (await lockout.TakeAsync(LockoutActivity.Restore)).Hold!;

            await Task.Delay(1000);
            var restarted = hold.TryStartWaiting();
            await Task.Delay(1000);

            Assert.Multiple(() =>
            {
                Assert.That(restarted, Is.True);
                Assert.That(hold.Ended.IsCancellationRequested, Is.False, "two seconds in, on a second step of a second");
                Assert.That(lockout.Active, Is.EqualTo(LockoutActivity.Restore));
            });
        }

        /// <summary>What the overlay counts down to: the end of the step being waited at.</summary>
        [Test]
        public async Task Hold_SaysWhenTheStepItWaitsAtRunsOut()
        {
            var lockout = new ChangeLockout { WaitLimit = TimeSpan.FromMinutes(5) };
            var before = DateTimeOffset.UtcNow;
            using var hold = (await lockout.TakeAsync(LockoutActivity.Restore)).Hold!;
            var after = DateTimeOffset.UtcNow;

            var whileWaiting = hold.WaitingUntil;
            hold.TryStartWork();
            var atWork = hold.WaitingUntil;

            Assert.Multiple(() =>
            {
                Assert.That(whileWaiting, Is.InRange(before + lockout.WaitLimit, after + lockout.WaitLimit));
                Assert.That(atWork, Is.Null);
            });
        }

        /// <summary>
        /// The pages hear of it both ways, and by then the lockout already says what they are
        /// about to draw. Giving a hold back twice tells them once.
        /// </summary>
        [Test]
        public async Task Changed_IsRaisedWhenTheHoldIsTakenAndWhenItIsGivenBack()
        {
            var lockout = new ChangeLockout();
            var heard = new List<LockoutActivity?>();
            lockout.Changed += () => heard.Add(lockout.Active);

            var hold = (await lockout.TakeAsync(LockoutActivity.Backup)).Hold!;
            hold.Dispose();
            hold.Dispose();

            Assert.That(heard, Is.EqualTo(new LockoutActivity?[] { LockoutActivity.Backup, null }));
        }

        /// <summary>A page that fails to take the news must not keep it from the others.</summary>
        [Test]
        public async Task Changed_APageThatFailsDoesNotKeepItFromTheRest()
        {
            var lockout = new ChangeLockout();
            var heard = 0;
            lockout.Changed += () => throw new InvalidOperationException("the page has gone");
            lockout.Changed += () => heard++;

            using (var hold = (await lockout.TakeAsync(LockoutActivity.Restore)).Hold)
            {
            }

            Assert.That(heard, Is.EqualTo(2));
        }
    }
}
