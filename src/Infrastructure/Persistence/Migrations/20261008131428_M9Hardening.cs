using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M9Hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LedgerBreakRecords",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BreakKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    TimesSeen = table.Column<int>(type: "int", nullable: false),
                    ClearedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerBreakRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerBreakRecords_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LedgerCheckpoints",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastEntryId = table.Column<long>(type: "bigint", nullable: false),
                    EntryCount = table.Column<long>(type: "bigint", nullable: false),
                    HeadHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    BalanceAfter = table.Column<int>(type: "int", nullable: false),
                    KeyId = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Mac = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerCheckpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerCheckpoints_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerCheckpoints_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalSchema: "licensing",
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LedgerVerificationRuns",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Trigger = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LicensesChecked = table.Column<int>(type: "int", nullable: false),
                    EntriesChecked = table.Column<long>(type: "bigint", nullable: false),
                    BrokenLicenses = table.Column<int>(type: "int", nullable: false),
                    BreaksJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerVerificationRuns", x => x.Id);
                    table.CheckConstraint("CK_LedgerVerificationRuns_Status", "[Status] IN ('Running', 'Completed', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "LicenseAdjustmentRequests",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LedgerTransactionId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseAdjustmentRequests", x => x.Id);
                    table.CheckConstraint("CK_LicenseAdjustmentRequests_Credits", "[Credits] <> 0");
                    table.CheckConstraint("CK_LicenseAdjustmentRequests_Status", "[Status] IN ('Pending', 'Approved', 'Rejected', 'Expired')");
                    table.ForeignKey(
                        name: "FK_LicenseAdjustmentRequests_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseAdjustmentRequests_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalSchema: "licensing",
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MfaChallenges",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MfaChallenges_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MfaChallenges_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MfaRecoveryCodes",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "varbinary(32)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MfaRecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MfaRecoveryCodes_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MfaRecoveryCodes_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserMfa",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecretEnc = table.Column<byte[]>(type: "varbinary(128)", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    LastUsedStep = table.Column<long>(type: "bigint", nullable: true),
                    ConfirmFailures = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMfa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMfa_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserMfa_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerBreakRecords_ClientId",
                schema: "licensing",
                table: "LedgerBreakRecords",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerBreakRecords_LicenseId_BreakKey",
                schema: "licensing",
                table: "LedgerBreakRecords",
                columns: new[] { "LicenseId", "BreakKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpoints_ClientId",
                schema: "licensing",
                table: "LedgerCheckpoints",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpoints_LicenseId_LastEntryId",
                schema: "licensing",
                table: "LedgerCheckpoints",
                columns: new[] { "LicenseId", "LastEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerVerificationRuns_StartedAt",
                schema: "licensing",
                table: "LedgerVerificationRuns",
                column: "StartedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerVerificationRuns_Status",
                schema: "licensing",
                table: "LedgerVerificationRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAdjustmentRequests_ClientId",
                schema: "licensing",
                table: "LicenseAdjustmentRequests",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAdjustmentRequests_LicenseId_RequestedAt",
                schema: "licensing",
                table: "LicenseAdjustmentRequests",
                columns: new[] { "LicenseId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseAdjustmentRequests_Status_ExpiresAt",
                schema: "licensing",
                table: "LicenseAdjustmentRequests",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MfaChallenges_ClientId",
                schema: "iam",
                table: "MfaChallenges",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_MfaChallenges_TokenHash",
                schema: "iam",
                table: "MfaChallenges",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MfaChallenges_UserId_ExpiresAt",
                schema: "iam",
                table: "MfaChallenges",
                columns: new[] { "UserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MfaRecoveryCodes_ClientId",
                schema: "iam",
                table: "MfaRecoveryCodes",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_MfaRecoveryCodes_UserId_CodeHash",
                schema: "iam",
                table: "MfaRecoveryCodes",
                columns: new[] { "UserId", "CodeHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserMfa_ClientId",
                schema: "iam",
                table: "UserMfa",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMfa_UserId",
                schema: "iam",
                table: "UserMfa",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerBreakRecords",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "LedgerCheckpoints",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "LedgerVerificationRuns",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "LicenseAdjustmentRequests",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "MfaChallenges",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "MfaRecoveryCodes",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "UserMfa",
                schema: "iam");
        }
    }
}
