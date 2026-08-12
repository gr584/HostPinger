using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HostPinger.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddResolverErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResolverErrors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolverErrors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResolverErrors_Address_TimestampUtc",
                table: "ResolverErrors",
                columns: new[] { "Address", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ResolverErrors_TimestampUtc",
                table: "ResolverErrors",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResolverErrors");
        }
    }
}
