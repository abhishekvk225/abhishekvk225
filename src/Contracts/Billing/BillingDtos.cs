using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Billing;

// ---- Client: configuration, packs, checkout, orders, profile ----------------------------------------------------------------

/// <summary>What the portal needs to decide whether to show the billing pages. Answered even when billing is switched off.</summary>
public sealed record BillingConfigDto(
    bool Enabled, decimal TaxPercent, string TaxLabel, IReadOnlyList<string> Currencies, bool RequireBillingProfile, bool RequireTaxId);

/// <summary>A credit pack as a signed-in client sees it. <c>priceMinor</c> is before tax; <c>totalMinor</c> is what is charged.</summary>
public sealed record CreditPackDto(
    Guid Id, string Name, string? Description, int Credits, int ValidityDays, long PriceMinor, string Currency,
    decimal TaxPercent, string TaxLabel, long TaxMinor, long TotalMinor, IReadOnlyList<string> Highlights);

/// <summary>A public pack for the pricing page (same figures, no ids a visitor could use for anything).</summary>
public sealed record PublicPackDto(
    Guid Id, string Name, string? Description, int Credits, int ValidityDays, long PriceMinor, string Currency,
    decimal TaxPercent, string TaxLabel, long TotalMinor, IReadOnlyList<string> Highlights);

/// <summary>The client never sends an amount: the price is read from the pack in the database.</summary>
public sealed record CheckoutRequest(Guid PackId, string? IdempotencyKey);

public sealed record CheckoutResponseDto(Guid OrderId, string CheckoutUrl, DateTime ExpiresAt);

public sealed record OrderListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    /// <summary>Pending, Paid, Failed, Expired, Cancelled, Refunded or PartiallyRefunded; empty = all.</summary>
    public string? Status { get; init; }
}

public sealed record OrderListItemDto(
    Guid Id, string? InvoiceNumber, string PackName, int Credits, long TotalMinor, string Currency, string Status, DateTime CreatedAt, DateTime? PaidAt);

/// <summary>One order. <c>licenseId</c> is set once the payment is confirmed and the credits are granted.</summary>
public sealed record OrderDto(
    Guid Id, string? InvoiceNumber, Guid PackId, string PackName, int Credits, int ValidityDays, long SubtotalMinor, long TaxMinor, long TotalMinor,
    decimal TaxPercent, string TaxLabel, string Currency, string Status, DateTime CreatedAt, DateTime? PaidAt, DateTime ExpiresAt, Guid? LicenseId,
    long RefundedMinor);

public sealed record BillingProfileDto(
    string LegalName, string AddressLine1, string? AddressLine2, string City, string? State, string PostalCode, string Country, string? TaxId,
    string BillingEmail, bool IsComplete);

public sealed record UpdateBillingProfileRequest(
    string LegalName, string AddressLine1, string? AddressLine2, string City, string? State, string PostalCode, string Country, string? TaxId,
    string BillingEmail);

// ---- Admin: packs, orders, refunds ---------------------------------------------------------------------------------------

/// <summary>Platform view of the billing configuration. <c>mode</c> is <c>sandbox</c>, <c>live</c>, <c>simulated</c> or <c>none</c>. No key material.</summary>
public sealed record AdminBillingConfigDto(
    bool Enabled, string Provider, string Mode, decimal TaxPercent, string TaxLabel, IReadOnlyList<string> Currencies, bool RequireBillingProfile);

public sealed record AdminCreditPackDto(
    Guid Id, string Name, string? Description, int Credits, int ValidityDays, long PriceMinor, string Currency, IReadOnlyList<string> Highlights,
    int DisplayOrder, bool IsActive, bool IsPublic, DateTime CreatedAt, DateTime? UpdatedAt);

public sealed record CreateCreditPackRequest(
    string Name, string? Description, int Credits, int ValidityDays, long PriceMinor, string Currency, IReadOnlyList<string>? Highlights,
    int DisplayOrder, bool IsActive, bool IsPublic);

public sealed record UpdateCreditPackRequest(
    string Name, string? Description, int Credits, int ValidityDays, long PriceMinor, string Currency, IReadOnlyList<string>? Highlights,
    int DisplayOrder, bool IsActive, bool IsPublic);

public sealed record AdminOrderQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;

    public Guid? ClientId { get; init; }

    public string? Status { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

public sealed record AdminOrderListItemDto(
    Guid Id, Guid ClientId, string ClientName, string? InvoiceNumber, string PackName, int Credits, long TotalMinor, long RefundedMinor, string Currency,
    string Status, string Provider, DateTime CreatedAt, DateTime? PaidAt);

public sealed record RefundDto(
    Guid Id, long AmountMinor, int CreditsRevoked, string Reason, string Status, string? ProviderRefundId, Guid? CreatedBy, DateTime CreatedAt);

public sealed record AdminOrderDto(
    OrderDto Order, Guid ClientId, string ClientName, string Provider, string? ProviderSessionId, string? ProviderPaymentId, int CreditsRevoked,
    string? FailureReason, IReadOnlyList<RefundDto> Refunds);

/// <summary>Refund an order. Without <c>amountMinor</c> the whole remaining amount is refunded.</summary>
public sealed record RefundOrderRequest(long? AmountMinor, string Reason);

/// <summary>Development and test hosts only: <c>success</c>, <c>failure</c> or <c>expired</c>.</summary>
public sealed record SimulatePaymentRequest(string Outcome);
