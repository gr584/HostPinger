using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HostPinger.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRoundtripToPartialPingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_PingAttempts_Unanswered",
                table: "PingAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc", "RoundtripMs" },
                filter: "\"RoundtripMs\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Unanswered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc", "RoundtripMs" },
                filter: "\"RoundtripMs\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts");

            migrationBuilder.DropIndex(
                name: "IX_PingAttempts_Unanswered",
                table: "PingAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc" },
                filter: "\"RoundtripMs\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Unanswered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc" },
                filter: "\"RoundtripMs\" IS NULL");
        }
    }
}
