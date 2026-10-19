using HostPinger.Components;

namespace HostPinger.Test
{
    /// <summary>
    /// Where a page's reads run and which of them it gets back. What a read does is not this
    /// class's business, so the reads here never touch a database: what is under test is the
    /// ordering around them, and each one is a gate the test opens when it wants that read to land.
    /// </summary>
    public class PageReadsTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private PageReads _reads = null!;

        [SetUp]
        public void SetUp() => _reads = new PageReads();

        /// <summary>
        /// The read a page's first render waits for. It happens inside the call, on the thread that
        /// made it, because a page that went on rendering without it would draw an empty table over
        /// the prerendered one.
        /// </summary>
        [Test]
        public async Task ReadAsync_RunsTheFirstReadOnTheThreadThatAsks()
        {
            var finishedInsideTheCall = false;
            var read = _reads.ReadAsync(
                _ =>
                {
                    finishedInsideTheCall = true;
                    return Task.FromResult("first");
                },
                CancellationToken.None);

            Assert.That(finishedInsideTheCall, Is.True);
            Assert.That(await read, Is.EqualTo("first"));
        }

        /// <summary>
        /// Every read after the first is sent off and the call comes straight back, which is what
        /// leaves the circuit free to answer clicks while a slow read is out. A read held on the
        /// caller's thread would never see the gate open, since opening it is the caller's next step.
        /// </summary>
        [Test]
        public async Task ReadAsync_SendsEveryLaterReadOffTheThreadThatAsks()
        {
            await _reads.ReadAsync(_ => Task.FromResult("first"), CancellationToken.None);

            using var gate = new ManualResetEventSlim();
            var later = _reads.ReadAsync(
                _ => Task.FromResult(gate.Wait(Patience) ? "later" : "held the caller"),
                CancellationToken.None);

            Assert.That(later.IsCompleted, Is.False);
            gate.Set();
            Assert.That(await later, Is.EqualTo("later"));
        }

        /// <summary>
        /// A page that starts from what its prerender read has drawn without reading, so its first
        /// read is not the one its first render waits for, and goes off like every later one.
        /// </summary>
        [Test]
        public async Task ReadAsync_SendsTheFirstReadAfterARestoreOffTheThreadThatAsks()
        {
            _reads.HandBackRestored();

            using var gate = new ManualResetEventSlim();
            var first = _reads.ReadAsync(
                _ => Task.FromResult(gate.Wait(Patience) ? "first" : "held the caller"),
                CancellationToken.None);

            Assert.That(first.IsCompleted, Is.False);
            gate.Set();
            Assert.That(await first, Is.EqualTo("first"));
        }

        /// <summary>
        /// A refresh sent before a click's own read, and landing after it, would put back what the
        /// database said before the click. It is dropped instead.
        /// </summary>
        [Test]
        public async Task ReadAsync_DropsAReadOvertakenByOneSentAfterIt()
        {
            await _reads.ReadAsync(_ => Task.FromResult("first"), CancellationToken.None);

            var olderLands = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var older = _reads.ReadAsync(_ => olderLands.Task, CancellationToken.None);
            var newer = _reads.ReadAsync(_ => Task.FromResult("newer"), CancellationToken.None);

            Assert.That(await newer.WaitAsync(Patience), Is.EqualTo("newer"));
            olderLands.SetResult("older");
            Assert.That(await older.WaitAsync(Patience), Is.Null);
        }

        /// <summary>
        /// Overlapping is not overtaking: reads that land in the order they were sent are each
        /// handed back, so a slow refresh still reaches the screen when nothing newer has.
        /// </summary>
        [Test]
        public async Task ReadAsync_HandsBackOverlappingReadsThatLandInOrder()
        {
            await _reads.ReadAsync(_ => Task.FromResult("first"), CancellationToken.None);

            var olderLands = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var newerLands = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var older = _reads.ReadAsync(_ => olderLands.Task, CancellationToken.None);
            var newer = _reads.ReadAsync(_ => newerLands.Task, CancellationToken.None);

            olderLands.SetResult("older");
            Assert.That(await older.WaitAsync(Patience), Is.EqualTo("older"));
            newerLands.SetResult("newer");
            Assert.That(await newer.WaitAsync(Patience), Is.EqualTo("newer"));
        }

        /// <summary>
        /// A page that has gone away cancels its token, and a read asked for after that is never
        /// sent. There is nothing to show it on, so the page gets the same null an overtaken read
        /// gets.
        /// </summary>
        [Test]
        public async Task ReadAsync_SendsNothingOnceCancelled()
        {
            await _reads.ReadAsync(_ => Task.FromResult("first"), CancellationToken.None);

            var sent = false;
            var after = await _reads.ReadAsync(
                _ =>
                {
                    sent = true;
                    return Task.FromResult("after");
                },
                new CancellationToken(canceled: true));

            Assert.That(after, Is.Null);
            Assert.That(sent, Is.False);
        }

        /// <summary>
        /// Leaving a graph sends one last read and then cancels it, and the read notices at its next
        /// check, as each bucket of a chart series does. That ends as a dropped read rather than as
        /// an exception for the page to pass up to Blazor.
        /// </summary>
        [Test]
        public async Task ReadAsync_DropsAReadCancelledWhileItIsOut()
        {
            await _reads.ReadAsync(_ => Task.FromResult("first"), CancellationToken.None);

            using var pageGoesAway = new CancellationTokenSource();
            var nextCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var last = _reads.ReadAsync(
                async cancellationToken =>
                {
                    await nextCheck.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                    return "last";
                },
                pageGoesAway.Token);

            await pageGoesAway.CancelAsync();
            nextCheck.SetResult();

            Assert.That(await last.WaitAsync(Patience), Is.Null);
        }

        /// <summary>
        /// Only the page's own cancellation means there is nothing to show. Any other one is a
        /// failure of the read itself, and comes out as one.
        /// </summary>
        [Test]
        public async Task ReadAsync_LetsACancellationThatIsNotThePagesOwnThrough()
        {
            await _reads.ReadAsync(_ => Task.FromResult("first"), CancellationToken.None);

            Assert.That(
                async () => await _reads.ReadAsync<string>(
                    _ => throw new OperationCanceledException("Not the page's."),
                    CancellationToken.None),
                Throws.InstanceOf<OperationCanceledException>());
        }
    }
}
