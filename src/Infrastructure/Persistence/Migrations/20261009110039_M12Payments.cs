using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M12Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LicenseAlerts_AlertType",
                schema: "licensing",
                table: "LicenseAlerts");

            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "BillingProfiles",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddressLine1 = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    AddressLine2 = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Country = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: false),
                    TaxId = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    BillingEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingProfiles_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CreditPacks",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    ValidityDays = table.Column<int>(type: "int", nullable: false),
                    PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Highlights = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditPacks", x => x.Id);
                    table.CheckConstraint("CK_CreditPacks_Amounts", "[Credits] > 0 AND [ValidityDays] > 0 AND [PriceMinor] > 0");
                });

            migrationBuilder.CreateTable(
                name: "InvoiceSequences",
                schema: "billing",
                columns: table => new
                {
                    Year = table.Column<int>(type: "int", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceSequences", x => x.Year);
                    table.CheckConstraint("CK_InvoiceSequences_Number", "[LastNumber] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    EventId = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    Outcome = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentOrders",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    ValidityDays = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    SubtotalMinor = table.Column<long>(type: "bigint", nullable: false),
                    TaxMinor = table.Column<long>(type: "bigint", nullable: false),
                    TotalMinor = table.Column<long>(type: "bigint", nullable: false),
                    TaxPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    TaxLabel = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    ProviderSessionId = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    ProviderPaymentId = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    CheckoutUrl = table.Column<string>(type: "varchar(2048)", unicode: false, maxLength: 2048, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    BuyerJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    RefundedMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreditsRevoked = table.Column<int>(type: "int", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOrders", x => x.Id);
                    table.CheckConstraint("CK_PaymentOrders_Amounts", "[SubtotalMinor] > 0 AND [TaxMinor] >= 0 AND [TotalMinor] = [SubtotalMinor] + [TaxMinor]");
                    table.CheckConstraint("CK_PaymentOrders_Refunds", "[RefundedMinor] >= 0 AND [RefundedMinor] <= [TotalMinor] AND [CreditsRevoked] >= 0 AND [CreditsRevoked] <= [Credits]");
                    table.CheckConstraint("CK_PaymentOrders_Status", "[Status] IN ('Pending', 'Paid', 'Failed', 'Expired', 'Cancelled', 'Refunded', 'PartiallyRefunded')");
                    table.ForeignKey(
                        name: "FK_PaymentOrders_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentOrders_CreditPacks_PackId",
                        column: x => x.PackId,
                        principalSchema: "billing",
                        principalTable: "CreditPacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentOrders_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalSchema: "licensing",
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreditsRevoked = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProviderRefundId = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refunds_Amount", "[AmountMinor] > 0 AND [CreditsRevoked] >= 0");
                    table.CheckConstraint("CK_Refunds_Status", "[Status] IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "FK_Refunds_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_PaymentOrders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "billing",
                        principalTable: "PaymentOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_LicenseAlerts_AlertType",
                schema: "licensing",
                table: "LicenseAlerts",
                sql: "[AlertType] IN ('LowBalance', 'Exhausted', 'Expiring', 'Expired', 'ApiKeyExpiring', 'PaymentReceived', 'PaymentRefunded')");

            migrationBuilder.CreateIndex(
                name: "IX_BillingProfiles_ClientId",
                schema: "billing",
                table: "BillingProfiles",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditPacks_IsPublic_IsActive_DisplayOrder",
                schema: "billing",
                table: "CreditPacks",
                columns: new[] { "IsPublic", "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_OrderId",
                schema: "billing",
                table: "PaymentEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_Provider_EventId",
                schema: "billing",
                table: "PaymentEvents",
                columns: new[] { "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_ReceivedAt",
                schema: "billing",
                table: "PaymentEvents",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_ClientId_CreatedAt",
                schema: "billing",
                table: "PaymentOrders",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_ClientId_IdempotencyKey",
                schema: "billing",
                table: "PaymentOrders",
                columns: new[] { "ClientId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_InvoiceNumber",
                schema: "billing",
                table: "PaymentOrders",
                column: "InvoiceNumber",
                unique: true,
                filter: "[InvoiceNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_LicenseId",
                schema: "billing",
                table: "PaymentOrders",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_PackId",
                schema: "billing",
                table: "PaymentOrders",
                column: "PackId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrders_Status_CreatedAt",
                schema: "billing",
                table: "PaymentOrders",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_ClientId",
                schema: "billing",
                table: "Refunds",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_OrderId",
                schema: "billing",
                table: "Refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_ProviderRefundId",
                schema: "billing",
                table: "Refunds",
                column: "ProviderRefundId",
                unique: true,
                filter: "[ProviderRefundId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingProfiles",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "InvoiceSequences",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "PaymentEvents",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "Refunds",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "PaymentOrders",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "CreditPacks",
                schema: "billing");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LicenseAlerts_AlertType",
                schema: "licensing",
                table: "LicenseAlerts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LicenseAlerts_AlertType",
                schema: "licensing",
                table: "LicenseAlerts",
                sql: "[AlertType] IN ('LowBalance', 'Exhausted', 'Expiring', 'Expired', 'ApiKeyExpiring')");
        }
    }
}
