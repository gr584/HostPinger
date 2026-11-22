using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HostPinger.Core.Migrations
{
    /// <summary>
    /// Stores each ping attempt's time as Unix milliseconds rather than as text; see the comment on
    /// the property in <c>HostPingerDbContext</c> for why, and <see cref="Data.StoredTime"/> for how.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite cannot change a column's type in place, and a column that keeps its TEXT type turns
    /// any integer stored in it back into text, so the table is rebuilt: a new one with the
    /// column as INTEGER, the attempts copied into it with their times converted, the old one
    /// dropped, the new one renamed into its place and the three indexes built over it.
    /// </para>
    /// <para>
    /// The old indexes are dropped first, before anything is written. SQLite takes a page for
    /// something new from the file's free pages before it grows the file, so the new table and
    /// indexes are written into the space the old ones had, and the file never grows: built with
    /// the old indexes still standing, the production copy's file would have gone from 1.0 GB to
    /// about 1.4, past its size limit, and the first round after the start would have pruned
    /// history to bring it back. What the old layout took beyond the new one is left as free pages,
    /// which later writes reuse and pruning's vacuum can give back to the disk.
    /// </para>
    /// <para>
    /// Written by hand rather than scaffolded. EF's own rebuild copies the column's values as they
    /// are, which would leave the text where it was, and it splits the work over three
    /// transactions: a start stopped between them would leave a half-built table that the next
    /// start could not tell from a finished one. Here it is all one transaction, which EF commits
    /// together with the migration's history row, so a start stopped partway leaves the file as it
    /// was and the next start does the whole of it again.
    /// </para>
    /// <para>
    /// The Ids are copied as they were, and the table's AUTOINCREMENT counter is carried over: a
    /// rebuild would otherwise restart it from the highest Id left, and hand out again the Ids of
    /// the newest attempts if a host delete had taken them. An attempt whose host no longer exists
    /// is left behind. Foreign keys are enforced on every connection that writes attempts, and a
    /// host's delete takes its attempts with it, so there should be none; but one there would fail
    /// the copy, and with it every start, for history nothing can show.
    /// </para>
    /// </remarks>
    public partial class StorePingTimesAsUnixMilliseconds : Migration
    {
        /// <summary>
        /// The milliseconds for a time as the provider writes it, <c>yyyy-MM-dd HH:mm:ss.FFFFFFF</c>:
        /// the whole seconds from strftime, and the first three of the fraction's digits, of which
        /// the provider drops trailing zeros, padded back out first. The rest of the fraction is
        /// dropped, which rounds down to the millisecond, as <see cref="Data.StoredTime"/> does;
        /// a round's attempts were all stamped with one time, and rounds are seconds apart, so no
        /// two attempts of a host come to share one. Exact, unlike julianday() or unixepoch(),
        /// which go through a double. A time strftime cannot read comes out NULL, which the NOT
        /// NULL column refuses, so a value in any other shape stops the migration rather than being
        /// stored wrong.
        /// </summary>
        internal const string MillisecondsFromText =
            "CAST(strftime('%s', substr(\"TimestampUtc\", 1, 19)) AS INTEGER) * 1000"
            + " + CAST(substr(substr(\"TimestampUtc\", 21) || '000', 1, 3) AS INTEGER)";

        /// <summary>
        /// The way back: milliseconds as the provider would have written the time. The millisecond
        /// within the second is taken as a remainder that stays positive, so that a time before
        /// 1970 splits into the second before it and a fraction, as the text has it, rather than
        /// SQLite's division rounding it towards zero.
        /// </summary>
        internal const string TextFromMilliseconds =
            "strftime('%Y-%m-%d %H:%M:%S', (\"TimestampUtc\" - ((\"TimestampUtc\" % 1000) + 1000) % 1000) / 1000, 'unixepoch')"
            + " || CASE WHEN ((\"TimestampUtc\" % 1000) + 1000) % 1000 = 0 THEN ''"
            + " ELSE '.' || rtrim(printf('%03d', ((\"TimestampUtc\" % 1000) + 1000) % 1000), '0') END";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Rebuild(migrationBuilder, "INTEGER", MillisecondsFromText);

            // Schema level 1: 1.14.0 and earlier cannot read the times now; see
            // HostPingerDatabase.SchemaLevel. In the same transaction as the rebuild, so the level
            // is raised exactly when the times are converted.
            migrationBuilder.Sql("PRAGMA user_version = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Rebuild(migrationBuilder, "TEXT", TextFromMilliseconds);
            migrationBuilder.Sql("PRAGMA user_version = 0;");
        }

        private static void Rebuild(MigrationBuilder migrationBuilder, string timestampType, string timestampFrom)
        {
            migrationBuilder.DropIndex(name: "IX_PingAttempts_HostId_TimestampUtc", table: "PingAttempts");
            migrationBuilder.DropIndex(name: "IX_PingAttempts_Unanswered", table: "PingAttempts");
            migrationBuilder.DropIndex(name: "IX_PingAttempts_Answered", table: "PingAttempts");

            migrationBuilder.Sql(
                $"""
                CREATE TABLE "ef_temp_PingAttempts" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_PingAttempts" PRIMARY KEY AUTOINCREMENT,
                    "HostId" INTEGER NOT NULL,
                    "TimestampUtc" {timestampType} NOT NULL,
                    "RoundtripMs" INTEGER NULL,
                    CONSTRAINT "FK_PingAttempts_Hosts_HostId" FOREIGN KEY ("HostId") REFERENCES "Hosts" ("Id") ON DELETE CASCADE
                );
                """);

            // In Id order, which is the order the rounds wrote them in, so the new table packs its
            // pages as the old one's were.
            migrationBuilder.Sql(
                $"""
                INSERT INTO "ef_temp_PingAttempts" ("Id", "HostId", "TimestampUtc", "RoundtripMs")
                SELECT "Id", "HostId", {timestampFrom}, "RoundtripMs"
                FROM "PingAttempts"
                WHERE "HostId" IN (SELECT "Id" FROM "Hosts")
                ORDER BY "Id";
                """);

            // The old table's counter, which is at least the highest Id it ever handed out. The
            // drop deletes the old table's row, and the rename renames this one.
            migrationBuilder.Sql(
                """
                DELETE FROM "sqlite_sequence" WHERE "name" = 'ef_temp_PingAttempts';
                INSERT INTO "sqlite_sequence" ("name", "seq")
                SELECT 'ef_temp_PingAttempts', "seq" FROM "sqlite_sequence" WHERE "name" = 'PingAttempts';
                """);

            migrationBuilder.Sql("""DROP TABLE "PingAttempts";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_PingAttempts" RENAME TO "PingAttempts";""");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_HostId_TimestampUtc",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Unanswered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc", "RoundtripMs" },
                filter: "\"RoundtripMs\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc", "RoundtripMs" },
                filter: "\"RoundtripMs\" IS NOT NULL");
        }
    }
}
