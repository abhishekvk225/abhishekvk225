using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLicensing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "licensing");

            migrationBuilder.CreateTable(
                name: "ClientCostRules",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    ChargePolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCostRules", x => x.Id);
                    table.CheckConstraint("CK_ClientCostRules_ChargePolicy", "[ChargePolicy] IN ('OnCompleted', 'OnSuccess', 'OnAttempt')");
                    table.CheckConstraint("CK_ClientCostRules_Credits", "[Credits] BETWEEN 0 AND 1000");
                    table.CheckConstraint("CK_ClientCostRules_Operation", "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");
                    table.ForeignKey(
                        name: "FK_ClientCostRules_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DefaultCredits = table.Column<int>(type: "int", nullable: false),
                    DefaultDurationDays = table.Column<int>(type: "int", nullable: false),
                    RateLimitPerMinute = table.Column<int>(type: "int", nullable: false),
                    DailyQuota = table.Column<int>(type: "int", nullable: true),
                    MaxFaceProfiles = table.Column<int>(type: "int", nullable: true),
                    MaxApiKeys = table.Column<int>(type: "int", nullable: false),
                    MaxUsers = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                    table.CheckConstraint("CK_Plans_Defaults", "[DefaultCredits] >= 0 AND [DefaultDurationDays] > 0");
                });

            migrationBuilder.CreateTable(
                name: "CostRules",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    ChargePolicy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostRules", x => x.Id);
                    table.CheckConstraint("CK_CostRules_ChargePolicy", "[ChargePolicy] IN ('OnCompleted', 'OnSuccess', 'OnAttempt')");
                    table.CheckConstraint("CK_CostRules_Credits", "[Credits] BETWEEN 0 AND 1000");
                    table.CheckConstraint("CK_CostRules_Operation", "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");
                    table.ForeignKey(
                        name: "FK_CostRules_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "licensing",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Licenses",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LicenseKey = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TotalCredits = table.Column<int>(type: "int", nullable: false),
                    ConsumedCredits = table.Column<int>(type: "int", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    SuspendedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    SuspendedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RenewedFromLicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licenses", x => x.Id);
                    table.CheckConstraint("CK_Licenses_Credits", "[TotalCredits] >= 0 AND [ConsumedCredits] >= 0 AND [ConsumedCredits] <= [TotalCredits]");
                    table.CheckConstraint("CK_Licenses_Period", "[ExpiresAt] > [StartsAt]");
                    table.CheckConstraint("CK_Licenses_Status", "[Status] IN ('Draft', 'Active', 'Inactive', 'Suspended', 'Expired', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_Licenses_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Licenses_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "licensing",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LicenseTransactions",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    BalanceBefore = table.Column<int>(type: "int", nullable: false),
                    BalanceAfter = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    RecognitionRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferenceTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorType = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    PrevHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    RowHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseTransactions", x => x.Id);
                    table.CheckConstraint("CK_LicenseTransactions_Balance", "[BalanceAfter] = [BalanceBefore] + [Credits] AND [BalanceAfter] >= 0");
                    table.CheckConstraint("CK_LicenseTransactions_Operation", "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");
                    table.CheckConstraint("CK_LicenseTransactions_Type", "[Type] IN ('Grant', 'Consume', 'Refund', 'Adjustment', 'Renewal', 'ExpiryWriteOff', 'Revocation')");
                    table.ForeignKey(
                        name: "FK_LicenseTransactions_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseTransactions_LicenseTransactions_ReferenceTransactionId",
                        column: x => x.ReferenceTransactionId,
                        principalSchema: "licensing",
                        principalTable: "LicenseTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseTransactions_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalSchema: "licensing",
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCostRules_ClientId_Operation_EffectiveFrom",
                schema: "licensing",
                table: "ClientCostRules",
                columns: new[] { "ClientId", "Operation", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_CostRules_PlanId_Operation_EffectiveFrom",
                schema: "licensing",
                table: "CostRules",
                columns: new[] { "PlanId", "Operation", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_ClientId_Status_ExpiresAt",
                schema: "licensing",
                table: "Licenses",
                columns: new[] { "ClientId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_LicenseKey",
                schema: "licensing",
                table: "Licenses",
                column: "LicenseKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_PlanId",
                schema: "licensing",
                table: "Licenses",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_Status_ExpiresAt",
                schema: "licensing",
                table: "Licenses",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_ClientId_CreatedAt",
                schema: "licensing",
                table: "LicenseTransactions",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_ClientId_IdempotencyKey",
                schema: "licensing",
                table: "LicenseTransactions",
                columns: new[] { "ClientId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_LicenseId_Id",
                schema: "licensing",
                table: "LicenseTransactions",
                columns: new[] { "LicenseId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_ReferenceTransactionId",
                schema: "licensing",
                table: "LicenseTransactions",
                column: "ReferenceTransactionId",
                unique: true,
                filter: "[ReferenceTransactionId] IS NOT NULL AND [Type] = 'Refund'");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Code",
                schema: "licensing",
                table: "Plans",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientCostRules",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "CostRules",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "LicenseTransactions",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "Licenses",
                schema: "licensing");

            migrationBuilder.DropTable(
                name: "Plans",
                schema: "licensing");
        }
    }
}
