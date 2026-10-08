using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LedgerUniqueChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LicenseTransactions_LicenseId_PrevHash",
                schema: "licensing",
                table: "LicenseTransactions",
                columns: new[] { "LicenseId", "PrevHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LicenseTransactions_LicenseId_PrevHash",
                schema: "licensing",
                table: "LicenseTransactions");
        }
    }
}
