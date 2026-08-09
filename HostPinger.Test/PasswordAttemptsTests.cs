using System.Net;
using System.Security.Cryptography;
using HostPinger.Core.Options;
using HostPinger.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace HostPinger.Test
{
    /// <summary>
    /// The wait that keeps somebody from working through a word list against the web UI password.
    /// Every case here is either a way of buying more guesses than the schedule allows, or a way of
    /// an ordinary person being made to wait when they should not be.
    /// </summary>
    public class PasswordAttemptsTests
    {
        private const string Password = "correct horse battery staple";

        private const string Wrong = "hunter2";

        private static readonly IPAddress Somebody = IPAddress.Parse("192.0.2.7");

        /// <summary>Long enough to outlast any wait the schedule imposes.</summary>
        private static readonly TimeSpan ADay = TimeSpan.FromDays(1);

        private StubClock _clock = null!;
        private PasswordAttempts _attempts = null!;

        [SetUp]
        public void SetUp()
        {
            _clock = new StubClock(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
            var options = new TestOptionsMonitor<SecurityOptions>(
                new SecurityOptions { PasswordHash = CheapHash(Password) });

            _attempts = new PasswordAttempts(
                new PasswordGate(options),
                NullLogger<PasswordAttempts>.Instance,
                _clock);
        }

        [Test]
        public void WaitFor_AsksNothingOfTheFirstFewWrongAnswers()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PasswordAttempts.WaitFor(0), Is.EqualTo(TimeSpan.Zero));
                Assert.That(PasswordAttempts.WaitFor(1), Is.EqualTo(TimeSpan.Zero));
                Assert.That(PasswordAttempts.WaitFor(3), Is.EqualTo(TimeSpan.Zero));
            });
        }

        [Test]
        public void WaitFor_DoublesWithEveryWrongAnswerAfterThose()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PasswordAttempts.WaitFor(4), Is.EqualTo(TimeSpan.FromSeconds(5)));
                Assert.That(PasswordAttempts.WaitFor(5), Is.EqualTo(TimeSpan.FromSeconds(10)));
                Assert.That(PasswordAttempts.WaitFor(6), Is.EqualTo(TimeSpan.FromSeconds(20)));
                Assert.That(PasswordAttempts.WaitFor(7), Is.EqualTo(TimeSpan.FromSeconds(40)));
                Assert.That(PasswordAttempts.WaitFor(11), Is.EqualTo(TimeSpan.FromSeconds(640)));
            });
        }

        /// <summary>
        /// The cap is what makes this a wait rather than a way of locking somebody out of their own
        /// monitoring for good, and the long run is there because the doubling would otherwise
        /// overflow on its way to an answer that was going to be the cap anyway.
        /// </summary>
        [Test]
        public void WaitFor_StopsAtAnHourHoweverLongTheRun()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PasswordAttempts.WaitFor(14), Is.EqualTo(TimeSpan.FromHours(1)));
                Assert.That(PasswordAttempts.WaitFor(100), Is.EqualTo(TimeSpan.FromHours(1)));
                Assert.That(PasswordAttempts.WaitFor(int.MaxValue), Is.EqualTo(TimeSpan.FromHours(1)));
            });
        }

        /// <summary>The schedule is a ceiling on guesses, so it is worth saying what that comes to.</summary>
        [Test]
        public void WaitFor_AllowsOnlyAFewDozenGuessesADay()
        {
            var elapsed = TimeSpan.Zero;
            var guesses = 0;
            while (elapsed < TimeSpan.FromDays(1))
            {
                elapsed += PasswordAttempts.WaitFor(++guesses);
            }

            Assert.That(guesses, Is.LessThan(40), "a day of uninterrupted guessing");
        }

        [Test]
        public void Verify_AcceptsTheRightPassword()
        {
            var attempt = _attempts.Verify(Somebody, Password);

            Assert.Multiple(() =>
            {
                Assert.That(attempt.IsAccepted, Is.True);
                Assert.That(attempt.Wait, Is.EqualTo(TimeSpan.Zero));
                Assert.That(attempt.WaitMessage, Is.Null);
            });
        }

        /// <summary>Mistyping a password from memory two or three times must cost nothing at all.</summary>
        [Test]
        public void Verify_AsksNothingOfTheFirstFewWrongAnswers()
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var result = _attempts.Verify(Somebody, Wrong);

                Assert.Multiple(() =>
                {
                    Assert.That(result.Result, Is.EqualTo(PasswordAttemptResult.Wrong), $"attempt {attempt}");
                    Assert.That(result.Wait, Is.EqualTo(TimeSpan.Zero), $"attempt {attempt}");
                });
            }
        }

        [Test]
        public void Verify_ImposesAWaitOnceThoseAreUsedUp()
        {
            GuessWrongly(3);

            var fourth = _attempts.Verify(Somebody, Wrong);

            Assert.Multiple(() =>
            {
                Assert.That(fourth.Result, Is.EqualTo(PasswordAttemptResult.Wrong));
                Assert.That(fourth.Wait, Is.EqualTo(TimeSpan.FromSeconds(5)));
                Assert.That(fourth.WaitMessage, Is.EqualTo("Too many wrong passwords. Try again in 5s."));
            });
        }

        /// <summary>
        /// The whole point: a guess made during a wait is not looked at, which is what caps how many
        /// can be made at all — and what keeps a flood of them from costing a hash apiece.
        /// </summary>
        [Test]
        public void Verify_TurnsAwayTheRightPasswordWhileTheWaitStands()
        {
            GuessWrongly(4);

            var attempt = _attempts.Verify(Somebody, Password);

            Assert.Multiple(() =>
            {
                Assert.That(attempt.IsAccepted, Is.False);
                Assert.That(attempt.Result, Is.EqualTo(PasswordAttemptResult.Refused));
                Assert.That(attempt.Wait, Is.EqualTo(TimeSpan.FromSeconds(5)));
            });
        }

        [Test]
        public void Verify_LooksAtGuessesAgainOnceTheWaitHasPassed()
        {
            GuessWrongly(4);
            _clock.Advance(TimeSpan.FromSeconds(5));

            var attempt = _attempts.Verify(Somebody, Password);

            Assert.That(attempt.IsAccepted, Is.True);
        }

        /// <summary>
        /// A wait that grew every time somebody hammered the form would let whoever is guessing hold
        /// the owner of the password out indefinitely, which is a worse problem than the one the
        /// wait is for.
        /// </summary>
        [Test]
        public void Verify_DoesNotLengthenTheWaitForGuessesMadeDuringIt()
        {
            GuessWrongly(4);

            _clock.Advance(TimeSpan.FromSeconds(4));
            for (var hammering = 0; hammering < 20; hammering++)
            {
                Assert.That(
                    _attempts.Verify(Somebody, Wrong).Result,
                    Is.EqualTo(PasswordAttemptResult.Refused));
            }

            // One second later the original five are up, and nothing that happened in between has
            // pushed that out.
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(_attempts.Verify(Somebody, Password).IsAccepted, Is.True);
        }

        /// <summary>Sitting out a wait buys one guess, not a fresh set of free ones.</summary>
        [Test]
        public void Verify_GoesOnDoublingAcrossAWaitThatWasSatOut()
        {
            GuessWrongly(4);
            _clock.Advance(TimeSpan.FromSeconds(5));

            var fifth = _attempts.Verify(Somebody, Wrong);

            Assert.That(fifth.Wait, Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void Verify_ForgivesAnAddressThatGetsItRight()
        {
            GuessWrongly(3);
            Assert.That(_attempts.Verify(Somebody, Password).IsAccepted, Is.True);

            // Back to the beginning: three more free, and no wait until the fourth.
            GuessWrongly(3);
            Assert.That(_attempts.Verify(Somebody, Wrong).Wait, Is.EqualTo(TimeSpan.FromSeconds(5)));
        }

        /// <summary>
        /// Somebody who gave up and came back tomorrow is a person, not an attack, and starts again
        /// with nothing owed.
        /// </summary>
        [Test]
        public void Verify_ForgetsAnAddressThatLeavesThePasswordAlone()
        {
            GuessWrongly(4);
            _clock.Advance(ADay);

            var attempt = _attempts.Verify(Somebody, Wrong);

            Assert.Multiple(() =>
            {
                Assert.That(attempt.Wait, Is.EqualTo(TimeSpan.Zero));
                Assert.That(_attempts.RememberedAddresses, Is.EqualTo(1), "the one it has just started over");
            });
        }

        /// <summary>
        /// Somebody guessing at every opportunity never gets that quiet spell, so the tally survives
        /// however long they keep at it — otherwise waiting out the cap would be a way of resetting
        /// it and the hour would be the ceiling rather than the floor.
        /// </summary>
        [Test]
        public void Verify_KeepsTheTallyOfAnAddressThatTakesEveryOpportunity()
        {
            GuessWrongly(4);

            for (var round = 0; round < 3; round++)
            {
                _clock.Advance(PasswordAttempts.WaitFor(4 + round));
                _attempts.Verify(Somebody, Wrong);
            }

            Assert.That(_attempts.Verify(Somebody, Wrong).Result, Is.EqualTo(PasswordAttemptResult.Refused));
            _clock.Advance(PasswordAttempts.WaitFor(7));
            Assert.That(_attempts.Verify(Somebody, Wrong).Wait, Is.EqualTo(PasswordAttempts.WaitFor(8)));
        }

        /// <summary>
        /// Counted per address, so that guessing badly from one machine cannot shut everybody else
        /// out of their own monitoring.
        /// </summary>
        [Test]
        public void Verify_CountsEachAddressOnItsOwn()
        {
            GuessWrongly(4);

            var elsewhere = _attempts.Verify(IPAddress.Parse("192.0.2.8"), Password);

            Assert.Multiple(() =>
            {
                Assert.That(elsewhere.IsAccepted, Is.True);
                Assert.That(
                    _attempts.Verify(Somebody, Password).Result,
                    Is.EqualTo(PasswordAttemptResult.Refused),
                    "and the one that was guessing is still waiting");
            });
        }

        /// <summary>
        /// One machine on IPv6 has billions of addresses to guess from, so a tally per address would
        /// be a tally of nothing.
        /// </summary>
        [Test]
        public void Verify_CountsAWholeIPv6PrefixAsOneAddress()
        {
            for (var host = 1; host <= 4; host++)
            {
                _attempts.Verify(IPAddress.Parse($"2001:db8::{host}"), Wrong);
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    _attempts.Verify(IPAddress.Parse("2001:db8::ffff"), Password).Result,
                    Is.EqualTo(PasswordAttemptResult.Refused),
                    "a fifth address in the same /64");
                Assert.That(
                    _attempts.Verify(IPAddress.Parse("2001:db8:0:1::1"), Password).IsAccepted,
                    Is.True,
                    "and the next /64 along is somebody else");
            });
        }

        /// <summary>Otherwise the same machine keeps two tallies by changing how it asks.</summary>
        [Test]
        public void Verify_TreatsAnIPv4MappedAddressAsTheAddressItStandsFor()
        {
            GuessWrongly(4);

            var mapped = _attempts.Verify(IPAddress.Parse("::ffff:192.0.2.7"), Password);

            Assert.That(mapped.Result, Is.EqualTo(PasswordAttemptResult.Refused));
        }

        /// <summary>An empty box cannot be right and tells whoever sent it nothing, so it costs nothing.</summary>
        [Test]
        public void Verify_DoesNotCountAnEmptyBoxAsAGuess()
        {
            for (var press = 0; press < 20; press++)
            {
                var attempt = _attempts.Verify(Somebody, string.Empty);

                Assert.Multiple(() =>
                {
                    Assert.That(attempt.IsAccepted, Is.False);
                    Assert.That(attempt.Wait, Is.EqualTo(TimeSpan.Zero));
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(_attempts.RememberedAddresses, Is.Zero);
                Assert.That(_attempts.Verify(Somebody, Password).IsAccepted, Is.True);
            });
        }

        /// <summary>
        /// Guesses sprayed one apiece from a great many addresses must not be a way of growing what
        /// this remembers until the service runs out of memory.
        /// </summary>
        [Test]
        public void Verify_RemembersOnlySoManyAddresses()
        {
            for (var address = 0; address < 4_000; address++)
            {
                _attempts.Verify(IPAddress.Parse($"2001:db8:{address / 256:x}:{address % 256:x}::1"), Wrong);
            }

            Assert.That(_attempts.RememberedAddresses, Is.LessThanOrEqualTo(1024));
        }

        /// <summary>
        /// And that spray must not be a way of clearing the tally of the address that has actually
        /// been working at it: what a flood crowds out is the rest of the flood.
        /// </summary>
        [Test]
        public void Verify_KeepsTheAddressWithMostToAnswerForWhenItRunsOutOfRoom()
        {
            GuessWrongly(8);

            for (var address = 0; address < 4_000; address++)
            {
                _attempts.Verify(IPAddress.Parse($"2001:db8:{address / 256:x}:{address % 256:x}::1"), Wrong);
            }

            Assert.That(_attempts.Verify(Somebody, Password).Result, Is.EqualTo(PasswordAttemptResult.Refused));
        }

        /// <summary>
        /// Requests arriving without an address at all — over a Unix socket, or in a test — share
        /// one tally rather than escaping the count.
        /// </summary>
        [Test]
        public void Verify_CountsRequestsWithNoAddressTogether()
        {
            for (var guess = 0; guess < 4; guess++)
            {
                _attempts.Verify(null, Wrong);
            }

            Assert.That(_attempts.Verify(null, Password).Result, Is.EqualTo(PasswordAttemptResult.Refused));
        }

        [Test]
        public void Waiting_ReadsAsATimeToWaitRatherThanNoneAtAll()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    PasswordAttempt.Waiting(TimeSpan.FromMilliseconds(120)),
                    Is.EqualTo("Too many wrong passwords. Try again in 1s."));
                Assert.That(
                    PasswordAttempt.Waiting(TimeSpan.FromSeconds(80)),
                    Is.EqualTo("Too many wrong passwords. Try again in 1m 20s."));
                Assert.That(
                    PasswordAttempt.Waiting(TimeSpan.FromHours(1)),
                    Is.EqualTo("Too many wrong passwords. Try again in 1h."));
            });
        }

        private void GuessWrongly(int times)
        {
            for (var guess = 0; guess < times; guess++)
            {
                _attempts.Verify(Somebody, Wrong);
            }
        }

        /// <summary>
        /// The stored password, in the format <see cref="PasswordHash"/> writes but over a single
        /// iteration.
        /// </summary>
        /// <remarks>
        /// The real cost is a tenth of a second, and the tests below make several thousand guesses
        /// between them, which at that price is several minutes of waiting for arithmetic that is
        /// not what any of them are about. Writing the format out here is the same bargain
        /// <see cref="PasswordHashTests"/> makes with the hash it has checked in: if the format
        /// ever changes underneath it, every test in this file fails at once and says so.
        /// </remarks>
        private static string CheapHash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, 32);
            return $"v1.1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(subkey)}";
        }

        /// <summary>A clock the test moves by hand, so a wait can be sat out without sitting it out.</summary>
        private sealed class StubClock(DateTimeOffset now) : TimeProvider
        {
            private DateTimeOffset _now = now;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
