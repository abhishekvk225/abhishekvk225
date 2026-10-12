using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexaVerify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M14Verification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecognitionRequests_Operation",
                schema: "face",
                table: "RecognitionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LicenseTransactions_Operation",
                schema: "licensing",
                table: "LicenseTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CostRules_Operation",
                schema: "licensing",
                table: "CostRules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClientCostRules_Operation",
                schema: "licensing",
                table: "ClientCostRules");

            migrationBuilder.EnsureSchema(
                name: "verification");

            migrationBuilder.CreateTable(
                name: "ModuleSettings",
                schema: "verification",
                columns: table => new
                {
                    ModuleKey = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModuleSettings", x => x.ModuleKey);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UploadState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExtractionState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RulesState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ConsistencyState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExternalState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExternalProviderId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    ExternalReference = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ExternalCompletedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    ReviewReasonCodesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Assurance = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SubmittedByType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SubmittedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConsentReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    DocumentSha256 = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: true),
                    CreditsCharged = table.Column<int>(type: "int", nullable: false),
                    LicenseTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    ProviderCreditsCharged = table.Column<int>(type: "int", nullable: false),
                    ProviderLicenseTransactionId = table.Column<long>(type: "bigint", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ProviderAttempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CancelRequestedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IpAddress = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    PurgedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                    table.CheckConstraint("CK_Requests_Assurance", "[Assurance] IN ('None', 'ExtractedOnly', 'ChecksCompleted', 'AuthoritativelyVerified')");
                    table.CheckConstraint("CK_Requests_ConsistencyState", "[ConsistencyState] IN ('NotStarted', 'Completed', 'Failed', 'Skipped')");
                    table.CheckConstraint("CK_Requests_Counters", "[CreditsCharged] >= 0 AND [ProviderCreditsCharged] >= 0 AND [Attempts] >= 0 AND [ProviderAttempts] >= 0");
                    table.CheckConstraint("CK_Requests_ExternalState", "[ExternalState] IN ('NotRequested', 'ProviderNotConfigured', 'Pending', 'Verified', 'NotVerified', 'Inconclusive', 'ProviderError', 'Timeout')");
                    table.CheckConstraint("CK_Requests_ExtractionState", "[ExtractionState] IN ('NotStarted', 'Completed', 'Failed', 'Skipped')");
                    table.CheckConstraint("CK_Requests_Lease", "([LeaseToken] IS NULL AND [LeaseUntil] IS NULL) OR ([LeaseToken] IS NOT NULL AND [LeaseUntil] IS NOT NULL)");
                    table.CheckConstraint("CK_Requests_RulesState", "[RulesState] IN ('NotStarted', 'Completed', 'Failed', 'Skipped')");
                    table.CheckConstraint("CK_Requests_Source", "[Source] IN ('Portal', 'Api')");
                    table.CheckConstraint("CK_Requests_Status", "[Status] IN ('Queued', 'Processing', 'Completed', 'ReviewRequired', 'Failed', 'Cancelled', 'Expired')");
                    table.CheckConstraint("CK_Requests_SubmittedByType", "[SubmittedByType] IN ('System', 'User', 'ApiKey', 'Platform')");
                    table.CheckConstraint("CK_Requests_Type", "[Type] IN ('BankStatement', 'Pan', 'Passport')");
                    table.CheckConstraint("CK_Requests_UploadState", "[UploadState] IN ('NotStarted', 'Completed', 'Failed', 'Skipped')");
                    table.ForeignKey(
                        name: "FK_Requests_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Checks",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Field = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checks", x => x.Id);
                    table.CheckConstraint("CK_Checks_Outcome", "[Outcome] IN ('Pass', 'Warn', 'Fail', 'NotApplicable', 'NotEvaluated')");
                    table.CheckConstraint("CK_Checks_Severity", "[Severity] IN ('Info', 'Warning', 'Review')");
                    table.CheckConstraint("CK_Checks_Stage", "[Stage] IN ('Extraction', 'Rules', 'Consistency', 'External')");
                    table.ForeignKey(
                        name: "FK_Checks_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Checks_Requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "verification",
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Extension = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    PurgeAfter = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    PurgedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.CheckConstraint("CK_Documents_Kind", "[Kind] IN ('Pdf', 'Jpeg', 'Png', 'WebP')");
                    table.CheckConstraint("CK_Documents_Size", "[SizeBytes] > 0 AND [PageCount] >= 0");
                    table.ForeignKey(
                        name: "FK_Documents_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Documents_Requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "verification",
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DownloadGrants",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Format = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    PrincipalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadGrants", x => x.Id);
                    table.CheckConstraint("CK_DownloadGrants_Kind", "[Kind] IN ('Report', 'TransactionsCsv', 'OriginalDocument')");
                    table.ForeignKey(
                        name: "FK_DownloadGrants_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DownloadGrants_Requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "verification",
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "verification",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Stage = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    NoteCode = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    ActorType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.CheckConstraint("CK_Events_ActorType", "[ActorType] IN ('System', 'User', 'ApiKey', 'Platform')");
                    table.CheckConstraint("CK_Events_FromStatus", "[FromStatus] IN ('Queued', 'Processing', 'Completed', 'ReviewRequired', 'Failed', 'Cancelled', 'Expired')");
                    table.CheckConstraint("CK_Events_ToStatus", "[ToStatus] IN ('Queued', 'Processing', 'Completed', 'ReviewRequired', 'Failed', 'Cancelled', 'Expired')");
                    table.ForeignKey(
                        name: "FK_Events_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Events_Requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "verification",
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Results",
                schema: "verification",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    DataEnc = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    IdentifierMasked = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IdentifierHash = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    FieldMatchesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RetainUntil = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Results", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_Results_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Results_Requests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "verification",
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentBlobs",
                schema: "verification",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentBlobs", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_DocumentBlobs_Clients_ClientId",
                        column: x => x.ClientId,
                        principalSchema: "tenancy",
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentBlobs_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "verification",
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecognitionRequests_Operation",
                schema: "face",
                table: "RecognitionRequests",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect', 'BankStatementExtraction', 'PanExtraction', 'PanProviderCheck', 'PassportExtraction', 'PassportProviderCheck')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LicenseTransactions_Operation",
                schema: "licensing",
                table: "LicenseTransactions",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect', 'BankStatementExtraction', 'PanExtraction', 'PanProviderCheck', 'PassportExtraction', 'PassportProviderCheck')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CostRules_Operation",
                schema: "licensing",
                table: "CostRules",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect', 'BankStatementExtraction', 'PanExtraction', 'PanProviderCheck', 'PassportExtraction', 'PassportProviderCheck')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClientCostRules_Operation",
                schema: "licensing",
                table: "ClientCostRules",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect', 'BankStatementExtraction', 'PanExtraction', 'PanProviderCheck', 'PassportExtraction', 'PassportProviderCheck')");

            migrationBuilder.CreateIndex(
                name: "IX_Checks_ClientId_RequestId_Ordinal",
                schema: "verification",
                table: "Checks",
                columns: new[] { "ClientId", "RequestId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_Checks_RequestId",
                schema: "verification",
                table: "Checks",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBlobs_ClientId",
                schema: "verification",
                table: "DocumentBlobs",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ClientId_PurgeAfter",
                schema: "verification",
                table: "Documents",
                columns: new[] { "ClientId", "PurgeAfter" },
                filter: "[PurgedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_ClientId_RequestId",
                schema: "verification",
                table: "Documents",
                columns: new[] { "ClientId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_RequestId",
                schema: "verification",
                table: "Documents",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_ClientId_RequestId",
                schema: "verification",
                table: "DownloadGrants",
                columns: new[] { "ClientId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_ExpiresAt",
                schema: "verification",
                table: "DownloadGrants",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_RequestId",
                schema: "verification",
                table: "DownloadGrants",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_TokenHash",
                schema: "verification",
                table: "DownloadGrants",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_ClientId_RequestId_Id",
                schema: "verification",
                table: "Events",
                columns: new[] { "ClientId", "RequestId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_RequestId",
                schema: "verification",
                table: "Events",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Claim",
                schema: "verification",
                table: "Requests",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] = 'Queued'")
                .Annotation("SqlServer:Include", new[] { "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ClientId_CreatedAt",
                schema: "verification",
                table: "Requests",
                columns: new[] { "ClientId", "CreatedAt" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ClientId_DocumentSha256",
                schema: "verification",
                table: "Requests",
                columns: new[] { "ClientId", "DocumentSha256" },
                filter: "[DocumentSha256] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ClientId_IdempotencyKey",
                schema: "verification",
                table: "Requests",
                columns: new[] { "ClientId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ClientId_Status_CreatedAt",
                schema: "verification",
                table: "Requests",
                columns: new[] { "ClientId", "Status", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ClientId_Type_Status_CreatedAt",
                schema: "verification",
                table: "Requests",
                columns: new[] { "ClientId", "Type", "Status", "CreatedAt" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Lease",
                schema: "verification",
                table: "Requests",
                columns: new[] { "Status", "LeaseUntil" },
                filter: "[Status] = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Retention",
                schema: "verification",
                table: "Requests",
                column: "ExpiresAt",
                filter: "[PurgedAt] IS NULL AND [ExpiresAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Stats",
                schema: "verification",
                table: "Requests",
                column: "CreatedAt",
                filter: "[IpAddress] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "ClientId", "Type", "Status", "ReviewRequired", "ExternalState", "ExternalProviderId", "ErrorCode", "CreditsCharged", "ProviderCreditsCharged", "StartedAt", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Results_ClientId_IdentifierHash",
                schema: "verification",
                table: "Results",
                columns: new[] { "ClientId", "IdentifierHash" },
                filter: "[IdentifierHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Results_ClientId_RetainUntil",
                schema: "verification",
                table: "Results",
                columns: new[] { "ClientId", "RetainUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Checks",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "DocumentBlobs",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "DownloadGrants",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "ModuleSettings",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "Results",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "Requests",
                schema: "verification");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecognitionRequests_Operation",
                schema: "face",
                table: "RecognitionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LicenseTransactions_Operation",
                schema: "licensing",
                table: "LicenseTransactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CostRules_Operation",
                schema: "licensing",
                table: "CostRules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ClientCostRules_Operation",
                schema: "licensing",
                table: "ClientCostRules");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecognitionRequests_Operation",
                schema: "face",
                table: "RecognitionRequests",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LicenseTransactions_Operation",
                schema: "licensing",
                table: "LicenseTransactions",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CostRules_Operation",
                schema: "licensing",
                table: "CostRules",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ClientCostRules_Operation",
                schema: "licensing",
                table: "ClientCostRules",
                sql: "[Operation] IN ('Enroll', 'Verify', 'Identify', 'Detect')");
        }
    }
}
