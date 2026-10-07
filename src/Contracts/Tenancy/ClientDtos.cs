using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Tenancy;

public sealed record ClientSummary(Guid Id, string Code, string Name, string Status, string TimeZone);

public sealed record ClientListItemDto(
    Guid Id, string Code, string Name, string ContactEmail, string Status, string? Country, DateTime CreatedAt, int UserCount);

public sealed record ClientDto(
    Guid Id, string Code, string Name, string? LegalName, string ContactEmail, string? ContactPhone,
    string? AddressLine1, string? AddressLine2, string? City, string? State, string? PostalCode, string? Country,
    string? Website, string? Industry, string TimeZone, string Status, string? StatusReason, DateTime? StatusChangedAt,
    string? Notes, DateTime CreatedAt, DateTime? UpdatedAt, string RowVersion);

public sealed record CreateClientRequest(
    string Code, string Name, string? LegalName, string ContactEmail, string? ContactPhone,
    string? AddressLine1, string? AddressLine2, string? City, string? State, string? PostalCode, string? Country,
    string? Website, string? Industry, string TimeZone, string? Notes,
    string AdminEmail, string AdminFullName);

public sealed record UpdateClientRequest(
    string Name, string? LegalName, string ContactEmail, string? ContactPhone,
    string? AddressLine1, string? AddressLine2, string? City, string? State, string? PostalCode, string? Country,
    string? Website, string? Industry, string TimeZone, string? Notes, string RowVersion);

public sealed record ClientStatusRequest(string? Reason);

/// <summary>Fields a client may edit about itself.</summary>
public sealed record UpdateClientProfileRequest(
    string Name, string? LegalName, string ContactEmail, string? ContactPhone,
    string? AddressLine1, string? AddressLine2, string? City, string? State, string? PostalCode, string? Country,
    string? Website, string? Industry, string TimeZone, string RowVersion);

public sealed record ClientUserDto(
    Guid Id, string Email, string FullName, string Status, string Role, string? JobTitle, bool IsOwner,
    DateTime? LastLoginAt, bool MustChangePassword, DateTime CreatedAt);

public sealed record CreateClientUserRequest(string Email, string FullName, string Role, string? JobTitle);

public sealed record UpdateClientUserRequest(string FullName, string Role, string? JobTitle, bool IsActive);

public sealed record AuditLogDto(
    long Id, DateTime OccurredAt, string Action, string EntityType, string EntityId, string ActorType, Guid? ActorId, string? IpAddress, string? CorrelationId);

public sealed record LoginHistoryDto(
    long Id, DateTime OccurredAt, string Outcome, string EmailAttempted, Guid? UserId, string? IpAddress, string? UserAgent, string? FailureReason);

public sealed record ActivityQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public string? Action { get; init; }

    public string? Outcome { get; init; }
}

public sealed record ClientListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    public string? Search { get; init; }

    public string? Status { get; init; }
}
