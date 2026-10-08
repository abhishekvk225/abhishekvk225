using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "api");

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                schema: "api",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    KeyPrefix = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Scopes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastUsedIp = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    RateLimitPerMinute = table.Column<int>(type: "int", nullable: true),
                    AllowedIps = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: false),
                    RotatedFromKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.CheckConstraint("CK_ApiKeys_Status", "[Status] IN ('Active', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_ApiKeys_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApiRequestLogs",
                schema: "api",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Method = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    RouteTemplate = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    RequestBytes = table.Column<long>(type: "bigint", nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiRequestLogs_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ClientId_Status",
                schema: "api",
                table: "ApiKeys",
                columns: new[] { "ClientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyPrefix",
                schema: "api",
                table: "ApiKeys",
                column: "KeyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_ApiKeyId_CreatedAt",
                schema: "api",
                table: "ApiRequestLogs",
                columns: new[] { "ApiKeyId", "CreatedAt" });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys",
                schema: "api");

            migrationBuilder.DropTable(
                name: "ApiRequestLogs",
                schema: "api");
        }
    }
}
