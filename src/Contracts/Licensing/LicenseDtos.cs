using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Licensing;

public sealed record LicenseListItemDto(
    Guid Id, Guid ClientId, string ClientName, string LicenseKey, string Name, string? PlanName, string EffectiveStatus,
    int TotalCredits, int ConsumedCredits, int RemainingCredits, DateTime StartsAt, DateTime ExpiresAt, int DaysRemaining);

public sealed record LicenseDto(
    Guid Id, Guid ClientId, string ClientName, string LicenseKey, string Name, Guid? PlanId, string? PlanName,
    string Status, string EffectiveStatus, int TotalCredits, int ConsumedCredits, int RemainingCredits, int UtilisationPercent,
    DateTime StartsAt, DateTime ExpiresAt, int DaysRemaining, DateTime? SuspendedAt, string? SuspendedReason,
    string? Notes, DateTime CreatedAt, DateTime? UpdatedAt, string RowVersion);

/// <summary>Credits and period default from the plan when omitted.</summary>
public sealed record CreateLicenseRequest(Guid? PlanId, string Name, int? TotalCredits, DateTime? StartsAt, DateTime? ExpiresAt, string? Notes);

public sealed record UpdateLicenseRequest(string Name, string? Notes, string RowVersion);

public sealed record LicenseReasonRequest(string? Reason);

public sealed record RenewLicenseRequest(DateTime ExpiresAt, int AdditionalCredits, string? Reason);

public sealed record AdjustLicenseRequest(int Credits, string Reason);

public sealed record RefundRequest(string Reason);

public sealed record LicenseTransactionDto(
    long Id, Guid LicenseId, string Type, int Credits, int BalanceBefore, int BalanceAfter, string? Operation,
    Guid? RecognitionRequestId, long? ReferenceTransactionId, string? Reason, string ActorType, Guid? ActorId, DateTime CreatedAt);

public sealed record LedgerVerificationDto(Guid LicenseId, bool Valid, int Entries, IReadOnlyList<string> Problems);

public sealed record LicenseListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    public Guid? ClientId { get; init; }

    public string? Status { get; init; }

    /// <summary>Only licenses that end within this many days (and are not already finished).</summary>
    public int? ExpiringInDays { get; init; }

    public string? Search { get; init; }
}

/// <summary>What a client sees: one plain-language picture of its credits.</summary>
public sealed record LicenseSummaryDto(
    string Health, string Message, int RemainingCredits, int TotalCredits, int ConsumedCredits, int UsablePercent,
    DateTime? NextExpiry, int? DaysUntilExpiry, int ActiveLicenses, IReadOnlyList<LicenseListItemDto> Licenses);

public sealed record PlanDto(
    Guid Id, string Code, string Name, string? Description, int DefaultCredits, int DefaultDurationDays, int RateLimitPerMinute,
    int? DailyQuota, int? MaxFaceProfiles, int MaxApiKeys, int MaxUsers, bool IsActive);

public sealed record SavePlanRequest(
    string Code, string Name, string? Description, int DefaultCredits, int DefaultDurationDays, int RateLimitPerMinute,
    int? DailyQuota, int? MaxFaceProfiles, int MaxApiKeys, int MaxUsers, bool IsActive);

public sealed record CostRuleDto(
    Guid Id, string Scope, Guid? ClientId, Guid? PlanId, string Operation, int Credits, string ChargePolicy,
    DateTime EffectiveFrom, DateTime? EffectiveTo);

public sealed record SetCostRuleRequest(string Operation, int Credits, string ChargePolicy, DateTime? EffectiveFrom);
