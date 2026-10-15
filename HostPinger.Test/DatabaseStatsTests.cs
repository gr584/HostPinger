using HostPinger.Core.Data;
using HostPinger.Core.Options;
using HostPinger.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HostPinger.Test
{
    public class DatabaseStatsTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<HostPingerDbContext> _options = null!;

        [SetUp]
        public void SetUp()
        {
            (_connection, _options) = TestDb.CreateInMemory();
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
        }

        [Test]
        public void Projections_UseAverageAttemptSizeAndRecordingRate()
        {
            // 200 attempts accounting for 100 bytes each, on top of the empty-database baseline.
            var stats = new DatabaseStats
            {
                SizeBytes = DatabaseStats.BaselineBytes + 20_000,
                MaxSizeBytes = 100 * 1024 * 1024,
                AttemptCount = 200,
                EnabledHostCount = 2,
                IntervalSeconds = 30,
            };

            Assert.Multiple(() =>
            {
                Assert.That(stats.AttemptsPerDay, Is.EqualTo(5_760));
                Assert.That(stats.BytesPerAttempt, Is.EqualTo(100));
                Assert.That(stats.GrowthBytesPerDay, Is.EqualTo(576_000));
                Assert.That(stats.CapacityDays, Is.EqualTo(100 * 1024 * 1024 / 576_000.0).Within(0.001));
            });
        }

        [Test]
        public void Projections_AreUnknownBelowTheMinimumSample()
        {
            var stats = new DatabaseStats
            {
                SizeBytes = DatabaseStats.BaselineBytes + 20_000,
                MaxSizeBytes = 100 * 1024 * 1024,
                AttemptCount = DatabaseStats.MinimumAttemptsForEstimate - 1,
                EnabledHostCount = 2,
                IntervalSeconds = 30,
            };

            Assert.Multiple(() =>
            {
                Assert.That(stats.BytesPerAttempt, Is.Null);
                Assert.That(stats.GrowthBytesPerDay, Is.Null);
                Assert.That(stats.CapacityDays, Is.Null);
            });
        }

        [Test]
        public void Projections_AreUnknownWithoutEnabledHosts()
        {
            var stats = new DatabaseStats
            {
                SizeBytes = DatabaseStats.BaselineBytes + 20_000,
                MaxSizeBytes = 100 * 1024 * 1024,
                AttemptCount = 200,
                EnabledHostCount = 0,
                IntervalSeconds = 30,
            };

            Assert.Multiple(() =>
            {
                Assert.That(stats.AttemptsPerDay, Is.Zero);
                Assert.That(stats.GrowthBytesPerDay, Is.Null);
                Assert.That(stats.CapacityDays, Is.Null);
            });
        }

        [Test]
        public void CapacityDays_IsUnboundedWhenPruningIsDisabled()
        {
            var stats = new DatabaseStats
            {
                SizeBytes = DatabaseStats.BaselineBytes + 20_000,
                MaxSizeBytes = 0,
                AttemptCount = 200,
                EnabledHostCount = 2,
                IntervalSeconds = 30,
            };

            Assert.Multiple(() =>
            {
                Assert.That(stats.GrowthBytesPerDay, Is.EqualTo(576_000));
                Assert.That(stats.CapacityDays, Is.Null);
            });
        }

        [Test]
        public async Task Collect_ReadsSizeCountsAndSettings()
        {
            await using (var db = new HostPingerDbContext(_options))
            {
                var enabled = new MonitoredHost { Name = "on", Address = "on.example" };
                var paused = new MonitoredHost { Name = "off", Address = "off.example", IsEnabled = false };
                db.Hosts.AddRange(enabled, paused);
                await db.SaveChangesAsync();

                db.PingAttempts.AddRange(Enumerable.Range(0, 50).Select(i => new PingAttempt
                {
                    HostId = enabled.Id,
                    TimestampUtc = DateTime.UtcNow.AddSeconds(-i),
                    RoundtripMs = i,
                }));
                await db.SaveChangesAsync();
            }

            await using var read = new HostPingerDbContext(_options);
            var stats = await DatabaseStats.CollectAsync(
                read,
                new PingerOptions { IntervalSeconds = 15, MaxDatabaseSizeMb = 10 });

            Assert.Multiple(() =>
            {
                Assert.That(stats.SizeBytes, Is.GreaterThan(0));
                Assert.That(stats.AttemptCount, Is.EqualTo(50));
                Assert.That(stats.EnabledHostCount, Is.EqualTo(1), "paused hosts do not add to the write rate");
                Assert.That(stats.IntervalSeconds, Is.EqualTo(15));
                Assert.That(stats.MaxSizeBytes, Is.EqualTo(10L * 1024 * 1024));
            });
        }

        /// <summary>
        /// The count is the span of the attempt ids, which is exact while they run unbroken: none
        /// at all is zero, and pruning, which takes the oldest, leaves an exact count of the rest.
        /// The history of a deleted host is what it still counts once gone, until pruning passes it.
        /// </summary>
        [Test]
        public async Task CountAttempts_IsTheSpanOfTheIds()
        {
            await using var db = new HostPingerDbContext(_options);
            var empty = await DatabaseStats.CountAttemptsAsync(db);

            var host = new MonitoredHost { Name = "host", Address = "host.example" };
            db.Hosts.Add(host);
            await db.SaveChangesAsync();
            var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            db.PingAttempts.AddRange(Enumerable.Range(0, 30).Select(i => new PingAttempt
            {
                HostId = host.Id,
                TimestampUtc = start.AddMinutes(i),
                RoundtripMs = i,
            }));
            await db.SaveChangesAsync();

            await db.PingAttempts.Where(a => a.TimestampUtc < start.AddMinutes(10)).ExecuteDeleteAsync();
            var afterPruning = await DatabaseStats.CountAttemptsAsync(db);

            // What a deleted host leaves: rows gone from the middle of the span, interleaved as its
            // attempts were with everyone else's.
            await db.PingAttempts
                .Where(a => a.TimestampUtc >= start.AddMinutes(15) && a.TimestampUtc < start.AddMinutes(20))
                .ExecuteDeleteAsync();
            var afterAHoleInTheMiddle = await DatabaseStats.CountAttemptsAsync(db);

            Assert.Multiple(() =>
            {
                Assert.That(empty, Is.Zero);
                Assert.That(afterPruning, Is.EqualTo(20), "the ten oldest are gone");
                Assert.That(afterAHoleInTheMiddle, Is.EqualTo(20), "a hole inside the span is still counted");
            });
        }

        /// <summary>
        /// Both ends of the span are seeks. Asked for in one SELECT, MIN and MAX are found by scanning
        /// the table, which is the cost the span is there to avoid; so is COUNT(*). On a large
        /// database that is not in memory, that scan is most of a minute.
        /// </summary>
        [Test]
        public async Task CountAttempts_SeeksBothEndsOfTheIds()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + DatabaseStats.AttemptIdSpanSql;
            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(3));
                }
            }

            var onAttempts = plan.Where(step => step.Contains("PingAttempts")).ToList();
            var detail = string.Join(" | ", plan);
            Assert.Multiple(() =>
            {
                Assert.That(onAttempts, Has.Count.EqualTo(2), $"One step for each end. Plan was: {detail}");
                Assert.That(onAttempts, Has.All.StartsWith("SEARCH"), $"Both ends should be seeks. Plan was: {detail}");
            });
        }
    }
}
