using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Verification;

// Conventions: enums travel as strings (Domain enum names). No DTO exposes OCR scores, name-match scores, raw provider payloads, full
// identifiers or stack traces, and none carries a boolean that claims a document is authentic/genuine/valid/verified (docs/07 Rule V-1).

// ---- Submission (form fields accompanying the multipart file part "document") --------------------------------------------------

/// <summary>Fields next to the uploaded statement. <c>ExternalRef</c> is the caller's own reference: do not put personal data in it.</summary>
public sealed record SubmitBankStatementRequest(
    string? ExternalRef, string? ConsentReference, string? AccountHolderName, string? BankName, string? AccountNumberLast4,
    DateOnly? PeriodFrom, DateOnly? PeriodTo);

/// <summary>Either a document or <c>PanNumber</c> must be supplied (number-only mode).</summary>
public sealed record SubmitPanRequest(
    string? ExternalRef, string? ConsentReference, string? PanNumber, string? FullName, DateOnly? DateOfBirth, string? FatherName);

/// <summary>The document is required for passports. <c>Nationality</c> is an ISO 3166 alpha-3 code.</summary>
public sealed record SubmitPassportRequest(
    string? ExternalRef, string? ConsentReference, string? PassportNumber, string? FullName, DateOnly? DateOfBirth, string? Nationality,
    DateOnly? DateOfIssue, DateOnly? DateOfExpiry);

public sealed record VerificationAcceptedDto(
    Guid RequestId, string Type, string Status, DateTime CreatedAt, string StatusUrl, int PollAfterSeconds, bool Replayed);

// ---- Status / result ----------------------------------------------------------------------------------------------------------

/// <summary>The independent outcome flags. <c>Completed</c> on a stage means it ran, not that every check passed.</summary>
public sealed record VerificationStagesDto(
    string Uploaded, string Extracted, string RulesChecked, string ConsistencyChecked, string ExternalVerification, string? ExternalProviderId);

public sealed record VerificationStatusDto(
    Guid RequestId, string Type, string Status, VerificationStagesDto Stages, bool ReviewRequired, string Assurance, string? ErrorCode,
    string? ErrorMessage, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, int CreditsCharged, string CorrelationId);

public sealed record VerificationDocumentDto(string Kind, int PageCount, int SizeBytes, bool Purged);

public sealed record VerificationSummaryDto(int Passed, int Warned, int Failed, int NotEvaluated);

/// <summary>Messages are PII-free template text.</summary>
public sealed record VerificationCheckDto(string Code, string Stage, string Outcome, string Severity, string? Field, string Message);

public sealed record VerificationReviewReasonDto(string Code, string Message);

/// <summary>Outcome only (Match, PartialMatch, Mismatch, NotProvided, NotExtracted); scores never leave the server.</summary>
public sealed record VerificationFieldMatchDto(string Field, string Outcome);

/// <summary>The authoritative provider result. Only <c>State = Verified</c> with a provider id and reference means officially verified.</summary>
public sealed record VerificationExternalDto(string State, string? ProviderId, string? Reference, DateTime? CompletedAt, string? Message);

public sealed record StageCreditDto(int Charged, long? TransactionId);

/// <summary>Present only when the caller also holds <c>license.read</c>.</summary>
public sealed record VerificationCreditsDto(StageCreditDto Extraction, StageCreditDto Provider);

public sealed record VerificationProcessingDto(DateTime? StartedAt, DateTime? CompletedAt, int Attempts);

public sealed record BankTransactionDto(
    int Index, DateOnly Date, DateOnly? ValueDate, string Description, string? Reference, decimal? Debit, decimal? Credit, decimal? Balance);

public sealed record BankStatementExtractedDto(
    string? BankName, [property: Sensitive] string? AccountHolderName, string? AccountNumberMasked, string Currency, DateOnly? PeriodFrom,
    DateOnly? PeriodTo, decimal? OpeningBalance, decimal? ClosingBalance, int TransactionCount, decimal? TotalCredits, decimal? TotalDebits,
    string TextSource, string RuleSetId, IReadOnlyList<BankTransactionDto> Transactions, bool TransactionsTruncated);

public sealed record PanExtractedDto(
    string PanMasked, string? HolderTypeCode, string? HolderType, [property: Sensitive] string? NameOnCard,
    [property: Sensitive] string? FatherName, DateOnly? DateOfBirth);

/// <summary>Per field: Valid, Invalid or NotPresent.</summary>
public sealed record MrzChecksDto(string DocumentNumber, string DateOfBirth, string DateOfExpiry, string PersonalNumber, string Composite);

public sealed record MrzDto(bool Present, string? Format, MrzChecksDto? Checks);

public sealed record PassportExtractedDto(
    string PassportNumberMasked, string? IssuingCountry, string? Nationality, [property: Sensitive] string? Surname,
    [property: Sensitive] string? GivenNames, DateOnly? DateOfBirth, string? Sex, DateOnly? DateOfIssue, DateOnly? DateOfExpiry, bool IsExpired,
    MrzDto Mrz);

/// <summary>Exactly one of the three is set; all are null when nothing was extracted or the data was purged.</summary>
public sealed record VerificationExtractedDto(BankStatementExtractedDto? BankStatement, PanExtractedDto? Pan, PassportExtractedDto? Passport);

public sealed record VerificationResultDto(
    Guid RequestId, string Type, string Status, VerificationStagesDto Stages, bool ReviewRequired, string Assurance, string? ErrorCode,
    string? ErrorMessage, DateTime CreatedAt, string CorrelationId, string? ExternalRef, VerificationDocumentDto? Document,
    VerificationSummaryDto Summary, IReadOnlyList<VerificationCheckDto> Checks, IReadOnlyList<VerificationReviewReasonDto> ReviewReasons,
    IReadOnlyList<VerificationFieldMatchDto> FieldMatches, VerificationExternalDto External, VerificationExtractedDto? Extracted,
    VerificationCreditsDto? Credits, VerificationProcessingDto Processing);

/// <summary>Printable report: the result plus a fixed disclaimer.</summary>
public sealed record VerificationReportDto(VerificationResultDto Result, DateTime GeneratedAt, string Disclaimer);

// ---- History / transactions ---------------------------------------------------------------------------------------------------

public sealed record VerificationListItemDto(
    Guid RequestId, string Type, string Status, bool ReviewRequired, string ExternalState, string Assurance, string? IdentifierMasked,
    string? ExternalRef, DateTime CreatedAt, DateTime? CompletedAt, int CreditsCharged);

public sealed record VerificationListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public string? Sort { get; init; }

    /// <summary>Request id prefix or the caller's external reference.</summary>
    public string? Search { get; init; }

    public string? Type { get; init; }

    public string? Status { get; init; }

    public bool? ReviewRequired { get; init; }

    public string? ExternalState { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

/// <summary>Bank-statement transactions page (<c>pageSize</c> up to 500).</summary>
public sealed record VerificationTransactionsQuery
{
    public const int MaxPageSize = 500;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 100;
}

// ---- Catalogue -----------------------------------------------------------------------------------------------------------------

public sealed record VerificationLimitsDto(int MaxFileBytes, IReadOnlyList<string> Extensions, int MaxPages);

/// <summary>Upcoming entries have <c>Available = false</c>, no required permissions and no limits: nothing can be submitted or charged.</summary>
public sealed record VerificationModuleDto(
    string Key, string DisplayName, string Description, string Status, bool Available, IReadOnlyList<string> RequiredPermissions,
    bool? ProviderConfigured, VerificationLimitsDto? Limits, string? PlannedNote);

// ---- Downloads -----------------------------------------------------------------------------------------------------------------

/// <summary><c>Kind</c> is Report, TransactionsCsv or OriginalDocument; <c>Format</c> (json or html) applies to reports.</summary>
public sealed record VerificationDownloadRequest(string Kind, string? Format);

public sealed record DownloadLinkDto(string DownloadUrl, DateTime ExpiresAt);

// ---- Platform admin (metadata only: never documents, extracted values, masked identifiers or checks) ----------------------------

public sealed record ProviderOutcomeCountsDto(int Verified, int NotVerified, int Inconclusive, int ProviderError, int Timeout);

public sealed record VerificationProviderStatusDto(string ProviderId, string Type, bool Configured, ProviderOutcomeCountsDto Last24Hours);

public sealed record ModuleCostDto(string Operation, int Credits, string ChargePolicy);

public sealed record AdminVerificationModuleDto(
    string Key, string DisplayName, string Description, string Status, bool Available, bool? IsEnabled, int DisplayOrder,
    IReadOnlyList<VerificationProviderStatusDto> Providers, IReadOnlyList<ModuleCostDto> Costs, DateTime? UpdatedAt, string? RowVersion);

public sealed record UpdateVerificationModuleRequest(bool IsEnabled, string Reason, string RowVersion);

public sealed record VerificationStatsQuery
{
    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public string? Type { get; init; }

    public Guid? ClientId { get; init; }
}

public sealed record VerificationCountDto(string Key, int Count);

public sealed record VerificationStatsDto(
    int Total, IReadOnlyList<VerificationCountDto> ByType, IReadOnlyList<VerificationCountDto> ByStatus, int ReviewRequired,
    IReadOnlyList<VerificationCountDto> FailuresByErrorCode, IReadOnlyList<VerificationCountDto> ProviderOutcomes,
    long ExtractionCreditsCharged, long ProviderCreditsCharged, double? P50ProcessingSeconds, double? P95ProcessingSeconds, int QueueDepth,
    double? OldestQueuedAgeSeconds);

public sealed record VerificationEventDto(string? FromStatus, string ToStatus, string? Stage, string? NoteCode, string ActorType, DateTime CreatedAt);

/// <summary>What platform staff may see about a tenant's verification. Deliberately free of personal and document data.</summary>
public sealed record AdminVerificationItemDto(
    Guid RequestId, Guid ClientId, string Type, string Status, VerificationStagesDto Stages, bool ReviewRequired, string Assurance, string? ErrorCode,
    int CreditsCharged, int ProviderCreditsCharged, int Attempts, string CorrelationId, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt);

public sealed record AdminVerificationDetailDto(AdminVerificationItemDto Item, IReadOnlyList<VerificationEventDto> Events);

public sealed record AdminVerificationListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public string? Type { get; init; }

    public string? Status { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

/// <summary>A platform-level audit row about the verification module (module switch, administrative view). Ids and codes only.</summary>
public sealed record AdminVerificationAuditItemDto(
    long Id, DateTime OccurredAt, string Action, string EntityType, string EntityId, string ActorType, Guid? ActorId, string? CorrelationId);

public sealed record AdminVerificationAuditQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}
