using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookEndpoints",
                schema: "api",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SecretEnc = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: false),
                    Events = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    DisabledAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    DisabledReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoints", x => x.Id);
                    table.CheckConstraint("CK_WebhookEndpoints_Status", "[Status] IN ('Active', 'Disabled')");
                    table.ForeignKey(
                        name: "FK_WebhookEndpoints_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                schema: "api",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.CheckConstraint("CK_WebhookDeliveries_Status", "[Status] IN ('Pending', 'Delivered', 'Abandoned')");
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_WebhookEndpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalSchema: "api",
                        principalTable: "WebhookEndpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_ClientId_CreatedAt",
                schema: "api",
                table: "WebhookDeliveries",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_EndpointId_CreatedAt",
                schema: "api",
                table: "WebhookDeliveries",
                columns: new[] { "EndpointId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_Status_NextAttemptAt",
                schema: "api",
                table: "WebhookDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEndpoints_ClientId_Status",
                schema: "api",
                table: "WebhookEndpoints",
                columns: new[] { "ClientId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookDeliveries",
                schema: "api");

            migrationBuilder.DropTable(
                name: "WebhookEndpoints",
                schema: "api");
        }
    }
}
