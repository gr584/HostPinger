using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HostPinger.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAnsweredPingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PingAttempts_TimestampUtc",
                table: "PingAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts",
                columns: new[] { "HostId", "TimestampUtc" },
                filter: "\"RoundtripMs\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PingAttempts_Answered",
                table: "PingAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_PingAttempts_TimestampUtc",
                table: "PingAttempts",
                column: "TimestampUtc");
        }
    }
}
