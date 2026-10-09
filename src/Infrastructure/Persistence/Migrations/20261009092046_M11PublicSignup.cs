using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M11PublicSignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                schema: "licensing",
                table: "Plans",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DisplayPrice",
                schema: "licensing",
                table: "Plans",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Highlights",
                schema: "licensing",
                table: "Plans",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                schema: "licensing",
                table: "Plans",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTrial",
                schema: "licensing",
                table: "Plans",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Deployments that predate the public website: the seeded TRIAL plan becomes the public free trial.
            migrationBuilder.Sql(
                "UPDATE [licensing].[Plans] SET [IsPublic] = 1, [IsTrial] = 1, [Highlights] = N'[\"Full API and web portal access\",\"Register, verify and identify faces\",\"No credit card required\"]' " +
                "WHERE [Code] = 'TRIAL' AND NOT EXISTS (SELECT 1 FROM [licensing].[Plans] WHERE [IsTrial] = 1)");

            migrationBuilder.CreateTable(
                name: "ContactRequests",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingSignups",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ResendCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingSignups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Plans_IsPublic_DisplayOrder",
                schema: "licensing",
                table: "Plans",
                columns: new[] { "IsPublic", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactRequests_CreatedAt",
                schema: "tenancy",
                table: "ContactRequests",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSignups_ExpiresAt",
                schema: "iam",
                table: "PendingSignups",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingSignups_NormalizedEmail",
                schema: "iam",
                table: "PendingSignups",
                column: "NormalizedEmail",
                unique: true,
                filter: "[ConsumedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactRequests",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "PendingSignups",
                schema: "iam");

            migrationBuilder.DropIndex(
                name: "IX_Plans_IsPublic_DisplayOrder",
                schema: "licensing",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "DisplayOrder",
                schema: "licensing",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "DisplayPrice",
                schema: "licensing",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "Highlights",
                schema: "licensing",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                schema: "licensing",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "IsTrial",
                schema: "licensing",
                table: "Plans");
        }
    }
}
