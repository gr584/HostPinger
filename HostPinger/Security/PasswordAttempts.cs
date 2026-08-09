using System.Net;
using System.Net.Sockets;
using HostPinger.Core.Data;

namespace HostPinger.Security
{
    /// <summary>What became of a guess at the password.</summary>
    public enum PasswordAttemptResult
    {
        /// <summary>It was right.</summary>
        Accepted,

        /// <summary>It was looked at, and it was wrong.</summary>
        Wrong,

        /// <summary>
        /// It was not looked at at all: the address it came from is sitting out a wait, and a right
        /// answer arriving during one is turned away along with the wrong ones.
        /// </summary>
        Refused,
    }

    /// <summary>
    /// A guess and what it cost.
    /// </summary>
    /// <param name="Result">Whether it was accepted, refused, or looked at and wrong.</param>
    /// <param name="Wait">
    /// How long the address it came from must now leave the password alone. Zero whenever it may go
    /// straight on guessing, which is the ordinary case.
    /// </param>
    public readonly record struct PasswordAttempt(PasswordAttemptResult Result, TimeSpan Wait)
    {
        public bool IsAccepted => Result == PasswordAttemptResult.Accepted;

        /// <summary>
        /// What to tell whoever sent it when a wait now stands between them and their next guess,
        /// and null when none does — which leaves the page free to say whatever it would have said.
        /// </summary>
        public string? WaitMessage => Wait > TimeSpan.Zero ? Waiting(Wait) : null;

        /// <summary>
        /// The sentence a wait is reported with, with <paramref name="wait"/> being what is left of
        /// it — so nothing left says to go ahead.
        /// </summary>
        /// <remarks>
        /// In one place because four forms say it, and because the overlay says it again every
        /// second as it counts a wait down, from what is left rather than from an attempt of its
        /// own. Rounded up, so that the last fraction of a second reads as a second to wait rather
        /// than as no wait at all — nothing but a wait that has genuinely run out reads that way.
        /// </remarks>
        public static string Waiting(TimeSpan wait) =>
            wait > TimeSpan.Zero
                ? "Too many wrong passwords. Try again in "
                    + $"{Downtime.FormatDuration(TimeSpan.FromSeconds(Math.Ceiling(wait.TotalSeconds)))}."
                : "Too many wrong passwords. You can try again now.";
    }

    /// <summary>
    /// Every check of the web UI password, and the rising wait it imposes on an address that keeps
    /// getting it wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pages check a password through here rather than through <see cref="PasswordGate.Verify"/>
    /// directly, so that no guess can be made without being counted: counting is not something a
    /// call site can forget to do, because there is nothing else for it to call.
    /// </para>
    /// <para>
    /// The wait doubles with every wrong answer past the first few and stops at an hour. That costs
    /// somebody who mistypes their own password nothing, costs somebody who mistypes it repeatedly
    /// a few seconds, and turns an unattended run through a word list into a couple of dozen guesses
    /// a day — which, against the ten a second the hash alone would allow, is the difference between
    /// a weak password falling in an afternoon and standing indefinitely.
    /// </para>
    /// <para>
    /// It is counted per address rather than for the service as a whole, so that somebody guessing
    /// cannot lock out whoever owns the password by guessing badly enough. The address is the one
    /// the connection was opened from — see <see cref="RequestOrigin"/> for why nothing forwarded
    /// is trusted — so everyone arriving through a reverse proxy shares one tally, and on such an
    /// install the wait is in practice for the whole service.
    /// </para>
    /// <para>
    /// Nothing is written down: a restart forgets every tally. Persisting it would mean a database
    /// write on every wrong password, to defend against an attacker who can already restart the
    /// service — and who at that point has no need to guess anything.
    /// </para>
    /// </remarks>
    public sealed class PasswordAttempts(
        PasswordGate gate,
        ILogger<PasswordAttempts> logger,
        TimeProvider? timeProvider = null)
    {
        /// <summary>
        /// Wrong answers that cost nothing. A password typed from memory is worth two or three
        /// tries before anything starts treating it as an attack.
        /// </summary>
        private const int FreeAttempts = 3;

        /// <summary>What the first wrong answer past the free ones earns; it doubles from there.</summary>
        private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// As long as a wait ever gets. Long enough that guessing is hopeless, short enough that
        /// somebody who has locked themselves out of their own monitoring is not locked out of it
        /// for the rest of the day.
        /// </summary>
        private static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

        /// <summary>
        /// How long an address has to leave the password alone, once its wait has run out, before
        /// its tally is dropped and it starts again with the free attempts.
        /// </summary>
        /// <remarks>
        /// Measured from the end of the wait rather than from the last guess, which is what makes
        /// the difference between the two cases. Somebody who gave up, went away and came back is
        /// forgiven; somebody guessing at every opportunity keeps their tally, because taking each
        /// opportunity is exactly what stops the quiet spell that would clear it.
        /// </remarks>
        private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

        /// <summary>
        /// How many addresses are remembered at once. See <see cref="MakeRoom"/>: the point of a
        /// bound is that guesses sprayed from a great many addresses cannot grow this without limit.
        /// </summary>
        private const int MaximumRemembered = 1024;

        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

        private readonly Dictionary<string, Tally> _remembered = [];

        private readonly Lock _lock = new();

        /// <summary>
        /// How many addresses are being remembered. Exposed so that the bound on it can be seen to
        /// hold, which is the one thing about this that cannot be observed from the outside.
        /// </summary>
        public int RememberedAddresses
        {
            get
            {
                lock (_lock)
                {
                    return _remembered.Count;
                }
            }
        }

        /// <summary>
        /// Checks <paramref name="password"/> against the one that is set, and counts the attempt
        /// against <paramref name="origin"/>.
        /// </summary>
        public PasswordAttempt Verify(IPAddress? origin, string? password)
        {
            // An empty box is not a guess: it cannot be right, it tells whoever sent it nothing,
            // and counting it would let a stray Enter start a wait.
            if (string.IsNullOrEmpty(password))
            {
                return new PasswordAttempt(PasswordAttemptResult.Wrong, TimeSpan.Zero);
            }

            var key = KeyFor(origin);
            var now = _time.GetUtcNow();
            TimeSpan wait;

            lock (_lock)
            {
                Forget(now);

                if (_remembered.TryGetValue(key, out var tally) && now < tally.OpenAt)
                {
                    // Turned away before the password is looked at, which is what makes the wait a
                    // wait rather than a hint — and what keeps a flood of guesses from costing a
                    // hash apiece. Nothing is added to the tally here: whoever is guessing must not
                    // be able to stretch a wait that the owner of the password is also sitting out.
                    var left = tally.OpenAt - now;
                    logger.LogDebug(
                        "Turned away a password attempt from {Address} with {Seconds:F0}s of its wait left.",
                        RequestOrigin.Describe(origin),
                        left.TotalSeconds);

                    return new PasswordAttempt(PasswordAttemptResult.Refused, left);
                }

                if (tally is null)
                {
                    MakeRoom();
                    tally = new Tally();
                    _remembered[key] = tally;
                }

                // Charged before the password is checked rather than after, so that a thousand
                // guesses sent at once cannot all pass the test above together and buy a thousand
                // tries for the price of one. Getting it right takes it all back, below.
                tally.Attempts++;
                wait = WaitFor(tally.Attempts);
                tally.OpenAt = now + wait;
                tally.ForgetAt = tally.OpenAt + ForgetAfter;
            }

            // Outside the lock: this is a tenth of a second of hashing, and holding every other
            // request behind one guess is the shape of problem this class exists to avoid.
            if (gate.Verify(password))
            {
                lock (_lock)
                {
                    _remembered.Remove(key);
                }

                return new PasswordAttempt(PasswordAttemptResult.Accepted, TimeSpan.Zero);
            }

            if (wait > TimeSpan.Zero)
            {
                // Not a line per attempt, and it cannot become one: another of these needs another
                // wrong answer, and another wrong answer needs the wait to have run out first.
                logger.LogWarning(
                    "Too many wrong passwords from {Address}; nothing more will be looked at from "
                    + "there for {Wait}.",
                    RequestOrigin.Describe(origin),
                    Downtime.FormatDuration(wait));
            }

            return new PasswordAttempt(PasswordAttemptResult.Wrong, wait);
        }

        /// <summary>
        /// The wait earned by <paramref name="attempts"/> wrong answers: nothing for the first few,
        /// then five seconds doubling with each one after that, and never more than an hour.
        /// </summary>
        /// <remarks>
        /// Public because the schedule is the whole policy, and a policy is worth being able to
        /// read, and test, as the one expression it is.
        /// </remarks>
        public static TimeSpan WaitFor(int attempts)
        {
            var beyondFree = attempts - FreeAttempts;
            if (beyondFree <= 0)
            {
                return TimeSpan.Zero;
            }

            // The doubling is capped before it is worked out rather than after: a long enough run
            // would otherwise overflow on its way to an answer that was going to be the cap anyway.
            var seconds = FirstWait.TotalSeconds * Math.Pow(2, Math.Min(beyondFree - 1, 32));
            return seconds < LongestWait.TotalSeconds ? TimeSpan.FromSeconds(seconds) : LongestWait;
        }

        /// <summary>Drops the addresses that have left the password alone long enough.</summary>
        private void Forget(DateTimeOffset now)
        {
            List<string>? stale = null;
            foreach (var (key, tally) in _remembered)
            {
                if (tally.ForgetAt <= now)
                {
                    (stale ??= []).Add(key);
                }
            }

            foreach (var key in stale ?? [])
            {
                _remembered.Remove(key);
            }
        }

        /// <summary>
        /// Makes room for one more address once <see cref="MaximumRemembered"/> are being kept.
        /// </summary>
        /// <remarks>
        /// What goes is whichever address is closest to being forgotten anyway, which is the one
        /// that has guessed least recently and least often — so guesses sprayed one apiece from
        /// thousands of addresses crowd out each other rather than the address that has earned a
        /// long wait. Forgetting an address can only ever forgive it: nothing here can lock out
        /// somebody who was not already locked out.
        /// </remarks>
        private void MakeRoom()
        {
            if (_remembered.Count < MaximumRemembered)
            {
                return;
            }

            _remembered.Remove(_remembered.MinBy(entry => entry.Value.ForgetAt).Key);
        }

        /// <summary>What a run of guesses is counted against.</summary>
        /// <remarks>
        /// A whole /64 for IPv6 rather than the single address: one machine there routinely holds
        /// billions of them, and counting each separately would be counting nothing at all. An IPv4
        /// address wearing an IPv6 coat is folded back to itself, so the same machine cannot keep
        /// two tallies by changing how it asks. Anything with no address — a Unix socket, a test —
        /// shares one.
        /// </remarks>
        private static string KeyFor(IPAddress? origin)
        {
            if (origin is null)
            {
                return "unknown";
            }

            if (origin.IsIPv4MappedToIPv6)
            {
                origin = origin.MapToIPv4();
            }

            return origin.AddressFamily == AddressFamily.InterNetworkV6
                ? Convert.ToHexString(origin.GetAddressBytes(), 0, 8)
                : origin.ToString();
        }

        /// <summary>What one address has done lately.</summary>
        private sealed class Tally
        {
            /// <summary>Guesses looked at since the last right one, or since it was last forgotten.</summary>
            public int Attempts;

            /// <summary>When the next guess from it will be looked at.</summary>
            public DateTimeOffset OpenAt;

            /// <summary>When it stops being remembered at all, if nothing more arrives from it.</summary>
            public DateTimeOffset ForgetAt;
        }
    }
}
