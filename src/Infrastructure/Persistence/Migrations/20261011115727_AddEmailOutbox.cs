using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSuppressions",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourceEmailId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSuppressions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboundEmails",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ToDomain = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ToName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    Template = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HasOneTimeToken = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    BodiesPurgedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundEmails", x => x.Id);
                    table.CheckConstraint("CK_OutboundEmails_Status", "[Status] IN ('Pending', 'Sending', 'Sent', 'Failed', 'Abandoned')");
                    table.CheckConstraint("CK_OutboundEmails_Template", "[Template] IN ('VerifyEmail', 'AlreadyRegistered', 'PasswordReset', 'PasswordChanged', 'UserInvite', 'MfaEnabled', 'MfaDisabled', 'MfaRecoveryCodeUsed', 'LicenseLowBalance', 'LicenseExpiring', 'PaymentReceipt', 'RefundNotice', 'ContactRequest', 'WebhookEndpointDisabled', 'AdminTest')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSuppressions_Address",
                schema: "tenancy",
                table: "EmailSuppressions",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSuppressions_CreatedAt",
                schema: "tenancy",
                table: "EmailSuppressions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_CreatedAt",
                schema: "tenancy",
                table: "OutboundEmails",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_LastAttemptAt",
                schema: "tenancy",
                table: "OutboundEmails",
                column: "LastAttemptAt",
                filter: "[LastAttemptAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_Status_LeaseUntil",
                schema: "tenancy",
                table: "OutboundEmails",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_Status_NextAttemptAt",
                schema: "tenancy",
                table: "OutboundEmails",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_ToAddress_CreatedAt",
                schema: "tenancy",
                table: "OutboundEmails",
                columns: new[] { "ToAddress", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSuppressions",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "OutboundEmails",
                schema: "tenancy");
        }
    }
}
