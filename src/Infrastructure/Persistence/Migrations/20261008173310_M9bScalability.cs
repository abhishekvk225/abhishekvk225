using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M9bScalability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AlertedAt",
                schema: "licensing",
                table: "LedgerBreakRecords",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReminderAt",
                schema: "licensing",
                table: "LedgerBreakRecords",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UsageCounters",
                schema: "api",
                columns: table => new
                {
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    KeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Bucket = table.Column<long>(type: "bigint", nullable: false),
                    Used = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageCounters", x => new { x.Kind, x.KeyId, x.Bucket });
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageCounters_Kind_UpdatedAt",
                schema: "api",
                table: "UsageCounters",
                columns: new[] { "Kind", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageCounters",
                schema: "api");

            migrationBuilder.DropColumn(
                name: "AlertedAt",
                schema: "licensing",
                table: "LedgerBreakRecords");

            migrationBuilder.DropColumn(
                name: "LastReminderAt",
                schema: "licensing",
                table: "LedgerBreakRecords");
        }
    }
}
