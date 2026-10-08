using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "face");

            migrationBuilder.CreateTable(
                name: "FaceProfiles",
                schema: "face",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DisplayNameEnc = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ConsentReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ConsentRecordedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RetentionUntil = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceProfiles", x => x.Id);
                    table.CheckConstraint("CK_FaceProfiles_Status", "[Status] IN ('Active', 'Disabled')");
                    table.ForeignKey(
                        name: "FK_FaceProfiles_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionRequests",
                schema: "face",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ThresholdUsed = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    BestScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    CandidateCount = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ModelVersion = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    InputImageSha256 = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    CreditsCharged = table.Column<int>(type: "int", nullable: false),
                    BalanceAfter = table.Column<int>(type: "int", nullable: true),
                    LicenseTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionRequests", x => x.Id);
                    table.CheckConstraint("CK_RecognitionRequests_Credits", "[CreditsCharged] >= 0");
                    table.CheckConstraint("CK_RecognitionRequests_Operation", "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");
                    table.CheckConstraint("CK_RecognitionRequests_Outcome", "[Outcome] IN ('Enrolled', 'Matched', 'NoMatch', 'NoFaceDetected', 'MultipleFaces', 'LowQuality', 'ProviderError', 'Rejected')");
                    table.CheckConstraint("CK_RecognitionRequests_Source", "[Source] IN ('Api', 'Portal')");
                    table.CheckConstraint("CK_RecognitionRequests_Status", "[Status] IN ('Completed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_RecognitionRequests_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FaceTemplates",
                schema: "face",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ModelVersion = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    EmbeddingEnc = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    QualityScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    ImageSha256 = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceTemplates", x => x.Id);
                    table.CheckConstraint("CK_FaceTemplates_Quality", "[QualityScore] BETWEEN 0 AND 1");
                    table.CheckConstraint("CK_FaceTemplates_Status", "[Status] IN ('Active', 'Superseded')");
                    table.ForeignKey(
                        name: "FK_FaceTemplates_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FaceTemplates_FaceProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalSchema: "face",
                        principalTable: "FaceProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchResults",
                schema: "face",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rank = table.Column<byte>(type: "tinyint", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    IsMatch = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchResults", x => new { x.RequestId, x.Rank });
                    table.ForeignKey(
                        name: "FK_MatchResults_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchResults_RecognitionRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "face",
                        principalTable: "RecognitionRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FaceProfiles_ClientId_ExternalRef",
                schema: "face",
                table: "FaceProfiles",
                columns: new[] { "ClientId", "ExternalRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FaceProfiles_ClientId_RetentionUntil",
                schema: "face",
                table: "FaceProfiles",
                columns: new[] { "ClientId", "RetentionUntil" },
                filter: "[RetentionUntil] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FaceProfiles_ClientId_Status",
                schema: "face",
                table: "FaceProfiles",
                columns: new[] { "ClientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_ClientId_ImageSha256",
                schema: "face",
                table: "FaceTemplates",
                columns: new[] { "ClientId", "ImageSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_ClientId_ProfileId",
                schema: "face",
                table: "FaceTemplates",
                columns: new[] { "ClientId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_ClientId_Provider_ModelVersion_Status",
                schema: "face",
                table: "FaceTemplates",
                columns: new[] { "ClientId", "Provider", "ModelVersion", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FaceTemplates_ProfileId",
                schema: "face",
                table: "FaceTemplates",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchResults_ClientId_ProfileId",
                schema: "face",
                table: "MatchResults",
                columns: new[] { "ClientId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionRequests_ClientId_CreatedAt",
                schema: "face",
                table: "RecognitionRequests",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionRequests_ClientId_IdempotencyKey",
                schema: "face",
                table: "RecognitionRequests",
                columns: new[] { "ClientId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionRequests_ClientId_Outcome_CreatedAt",
                schema: "face",
                table: "RecognitionRequests",
                columns: new[] { "ClientId", "Outcome", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionRequests_ClientId_TargetProfileId_CreatedAt",
                schema: "face",
                table: "RecognitionRequests",
                columns: new[] { "ClientId", "TargetProfileId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FaceTemplates",
                schema: "face");

            migrationBuilder.DropTable(
                name: "MatchResults",
                schema: "face");

            migrationBuilder.DropTable(
                name: "FaceProfiles",
                schema: "face");

            migrationBuilder.DropTable(
                name: "RecognitionRequests",
                schema: "face");
        }
    }
}
