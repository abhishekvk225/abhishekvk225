using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageDashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RecognitionRequests_ClientId_CreatedAt",
                schema: "face",
                table: "RecognitionRequests");

            migrationBuilder.DropIndex(
                name: "IX_LicenseTransactions_ClientId_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ApiRequestLogs_ClientId_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_ApiRequestLogs_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs");

            migrationBuilder.CreateTable(
                name: "LicenseAlerts",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Bucket = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseAlerts", x => x.Id);
                    table.CheckConstraint("CK_LicenseAlerts_AlertType", "[AlertType] IN ('LowBalance', 'Exhausted', 'Expiring', 'Expired', 'ApiKeyExpiring')");
                    table.CheckConstraint("CK_LicenseAlerts_Severity", "[Severity] IN ('Info', 'Warning', 'Critical')");
                    table.ForeignKey(
                        name: "FK_LicenseAlerts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_Status_CreatedAt",
                schema: "api",
                table: "WebhookDeliveries",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionRequests_ClientId_CreatedAt",
                schema: "face",
                table: "RecognitionRequests",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Operation", "Outcome", "CreditsCharged" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_ClientId_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions",
                columns: new[] { "ClientId", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "Type", "Credits", "Operation" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions",
                column: "CreatedAt")
                .Annotation("SqlServer:Include", new[] { "ClientId", "Type", "Credits", "Operation" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_ClientId_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "StatusCode", "DurationMs", "ApiKeyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs",
                column: "CreatedAt")
                .Annotation("SqlServer:Include", new[] { "StatusCode", "DurationMs" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAlerts_ClientId_CreatedAt",
                schema: "licensing",
                table: "LicenseAlerts",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAlerts_SubjectId_AlertType_Bucket",
                schema: "licensing",
                table: "LicenseAlerts",
                columns: new[] { "SubjectId", "AlertType", "Bucket" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseAlerts",
                schema: "licensing");

            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveries_Status_CreatedAt",
                schema: "api",
                table: "WebhookDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_RecognitionRequests_ClientId_CreatedAt",
                schema: "face",
                table: "RecognitionRequests");

            migrationBuilder.DropIndex(
                name: "IX_LicenseTransactions_ClientId_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_LicenseTransactions_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ApiRequestLogs_ClientId_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs");

            migrationBuilder.DropIndex(
                name: "IX_ApiRequestLogs_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionRequests_ClientId_CreatedAt",
                schema: "face",
                table: "RecognitionRequests",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_ClientId_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_ClientId_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs",
                column: "CreatedAt");
        }
    }
}
