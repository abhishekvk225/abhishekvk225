using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M13KeyRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerCheckpoints_LicenseId_LastEntryId",
                schema: "licensing",
                table: "LedgerCheckpoints");

            migrationBuilder.CreateTable(
                name: "LedgerCheckpointSupersessions",
                schema: "licensing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckpointId = table.Column<long>(type: "bigint", nullable: false),
                    ReplacedByCheckpointId = table.Column<long>(type: "bigint", nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerCheckpointSupersessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerCheckpointSupersessions_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerCheckpointSupersessions_LedgerCheckpoints_CheckpointId",
                        column: x => x.CheckpointId,
                        principalSchema: "licensing",
                        principalTable: "LedgerCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerCheckpointSupersessions_LedgerCheckpoints_ReplacedByCheckpointId",
                        column: x => x.ReplacedByCheckpointId,
                        principalSchema: "licensing",
                        principalTable: "LedgerCheckpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpoints_LicenseId_LastEntryId_KeyId",
                schema: "licensing",
                table: "LedgerCheckpoints",
                columns: new[] { "LicenseId", "LastEntryId", "KeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpointSupersessions_CheckpointId",
                schema: "licensing",
                table: "LedgerCheckpointSupersessions",
                column: "CheckpointId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpointSupersessions_ClientId",
                schema: "licensing",
                table: "LedgerCheckpointSupersessions",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpointSupersessions_LicenseId",
                schema: "licensing",
                table: "LedgerCheckpointSupersessions",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpointSupersessions_ReplacedByCheckpointId",
                schema: "licensing",
                table: "LedgerCheckpointSupersessions",
                column: "ReplacedByCheckpointId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerCheckpointSupersessions",
                schema: "licensing");

            migrationBuilder.DropIndex(
                name: "IX_LedgerCheckpoints_LicenseId_LastEntryId_KeyId",
                schema: "licensing",
                table: "LedgerCheckpoints");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerCheckpoints_LicenseId_LastEntryId",
                schema: "licensing",
                table: "LedgerCheckpoints",
                columns: new[] { "LicenseId", "LastEntryId" },
                unique: true);
        }
    }
}
