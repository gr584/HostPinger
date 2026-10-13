using HostPinger.Components;

namespace HostPinger.Test
{
    /// <summary>
    /// The store a prerender leaves its read in for the live page that follows: taken once, by the
    /// id it was held under, while it is fresh, and never more of them held than the store allows.
    /// </summary>
    public class PrerenderHandoffTests
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

        private StubClock _clock = null!;
        private PrerenderHandoff _handoff = null!;

        [SetUp]
        public void SetUp()
        {
            _clock = new StubClock(Start);
            _handoff = new PrerenderHandoff(_clock);
        }

        /// <summary>The live page that follows its prerender finds the read the prerender left.</summary>
        [Test]
        public void Take_HandsBackWhatWasHeld()
        {
            var read = new Read("the prerender's");

            var id = _handoff.Hold(read);

            Assert.That(_handoff.Take<Read>(id!), Is.SameAs(read));
        }

        /// <summary>
        /// A read is one page's. Once taken it is gone, so a second circuit presenting the same id —
        /// the page opened again from history, say — reads for itself.
        /// </summary>
        [Test]
        public void Take_HandsAReadBackOnce()
        {
            var id = _handoff.Hold(new Read("once"));

            _handoff.Take<Read>(id!);

            Assert.That(_handoff.Take<Read>(id!), Is.Null);
        }

        /// <summary>
        /// A page prerendered and then not opened leaves its read to go stale; a circuit that turns
        /// up for it after half a minute reads for itself rather than draw it.
        /// </summary>
        [Test]
        public void Take_HandsNothingBackOnceTheReadHasGoneStale()
        {
            var id = _handoff.Hold(new Read("stale"));

            _clock.Advance(TimeSpan.FromSeconds(30));

            Assert.That(_handoff.Take<Read>(id!), Is.Null);
        }

        /// <summary>
        /// An id that was never handed out, or whose read is of another kind, finds nothing — and a
        /// read asked for as the wrong kind is let go of all the same.
        /// </summary>
        [Test]
        public void Take_HandsNothingBackForAnUnknownIdOrAnotherKindOfRead()
        {
            var id = _handoff.Hold(new Read("a graph's"));

            Assert.Multiple(() =>
            {
                Assert.That(_handoff.Take<Read>("not an id it handed out"), Is.Null);
                Assert.That(_handoff.Take<string>(id!), Is.Null);
                Assert.That(_handoff.Take<Read>(id!), Is.Null, "let go of by the attempt as the wrong kind");
            });
        }

        /// <summary>
        /// Requests for pages no browser goes on to open cannot pile reads up: past the cap, a hold is
        /// refused and that page reads for itself, until the reads already held go stale and are
        /// swept.
        /// </summary>
        [Test]
        public void Hold_RefusesPastItsCapacityUntilHeldReadsGoStale()
        {
            var held = Enumerable.Range(0, 64).Select(i => _handoff.Hold(new Read($"page {i}"))).ToList();

            var refused = _handoff.Hold(new Read("one too many"));
            _clock.Advance(TimeSpan.FromSeconds(30));
            var accepted = _handoff.Hold(new Read("after the sweep"));

            Assert.Multiple(() =>
            {
                Assert.That(held, Has.All.Not.Null);
                Assert.That(refused, Is.Null);
                Assert.That(accepted, Is.Not.Null);
            });
        }

        private sealed record Read(string What);

        private sealed class StubClock(DateTimeOffset now) : TimeProvider
        {
            private DateTimeOffset _now = now;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
