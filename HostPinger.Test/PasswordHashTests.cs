using HostPinger.Core.Options;

namespace HostPinger.Test
{
    public class PasswordHashTests
    {
        private const string Password = "correct horse battery staple";

        [Test]
        public void Verify_AcceptsThePasswordTheHashWasMadeFrom()
        {
            Assert.That(PasswordHash.Verify(PasswordHash.Hash(Password), Password), Is.True);
        }

        [Test]
        public void Verify_RejectsAnythingElse()
        {
            var stored = PasswordHash.Hash(Password);
            Assert.Multiple(() =>
            {
                Assert.That(PasswordHash.Verify(stored, "Correct horse battery staple"), Is.False);
                Assert.That(PasswordHash.Verify(stored, Password + " "), Is.False);
                Assert.That(PasswordHash.Verify(stored, string.Empty), Is.False);
            });
        }

        /// <summary>
        /// The overlay is a plain JSON file meant to be edited by hand, so a value that has been
        /// truncated, half-deleted or never written has to leave the application locked and
        /// working rather than failing on every attempt.
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("hunter2")]
        [TestCase("v1.210000.AAECAwQFBgcICQoLDA0ODw==")]
        [TestCase("v2.210000.AAECAwQFBgcICQoLDA0ODw==.GEZcreCWwYW19gdliR/KP3RHfiP9m2/Ij694MakXU6w=")]
        [TestCase("v1.notanumber.AAECAwQFBgcICQoLDA0ODw==.GEZcreCWwYW19gdliR/KP3RHfiP9m2/Ij694MakXU6w=")]
        [TestCase("v1.0.AAECAwQFBgcICQoLDA0ODw==.GEZcreCWwYW19gdliR/KP3RHfiP9m2/Ij694MakXU6w=")]
        [TestCase("v1.210000.not base64.also not base64")]
        [TestCase("v1.210000..")]
        public void Verify_RejectsAnUnreadableStoredValueWithoutThrowing(string? stored)
        {
            Assert.That(PasswordHash.Verify(stored, Password), Is.False);
        }

        [Test]
        public void Hash_SaltsEachOneSeparately()
        {
            var first = PasswordHash.Hash(Password);
            var second = PasswordHash.Hash(Password);

            Assert.That(second, Is.Not.EqualTo(first),
                "the same password hashed twice must not produce the same stored value");
        }

        /// <summary>
        /// A stored value produced before this test was written, which every later build has to go
        /// on accepting: the overlay survives upgrades, and a change to the format that nobody
        /// noticed would lock every existing install out of its own settings.
        /// </summary>
        [Test]
        public void Verify_AcceptsAValueWrittenByAnEarlierBuild()
        {
            const string stored =
                "v1.210000.AAECAwQFBgcICQoLDA0ODw==.GEZcreCWwYW19gdliR/KP3RHfiP9m2/Ij694MakXU6w=";

            Assert.That(PasswordHash.Verify(stored, Password), Is.True);
        }

        [Test]
        public void Stamp_IsEmptyWhenNothingIsStored()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PasswordHash.Stamp(null), Is.Empty);
                Assert.That(PasswordHash.Stamp(string.Empty), Is.Empty);
            });
        }

        [Test]
        public void Stamp_FollowsTheStoredValue()
        {
            var stored = PasswordHash.Hash(Password);
            var stamp = PasswordHash.Stamp(stored);

            Assert.Multiple(() =>
            {
                Assert.That(stamp, Is.Not.Empty);
                Assert.That(PasswordHash.Stamp(stored), Is.EqualTo(stamp), "the same stored value must stamp the same");
                Assert.That(PasswordHash.Stamp(PasswordHash.Hash(Password)), Is.Not.EqualTo(stamp),
                    "a new hash of the same password must still invalidate the unlocks issued under the old one");
            });
        }

        /// <summary>The stamp is a fingerprint, so what the browser holds is not the stored value.</summary>
        [Test]
        public void Stamp_DoesNotContainTheStoredValue()
        {
            var stored = PasswordHash.Hash(Password);

            Assert.That(stored, Does.Not.Contain(PasswordHash.Stamp(stored)));
        }
    }
}
