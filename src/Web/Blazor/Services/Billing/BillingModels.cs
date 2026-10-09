using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Web.Services;

/// <summary>A credit pack a signed-in client can buy (<c>GET /client/billing/packs</c>). Every amount comes from the API.</summary>
public sealed record CreditPackDto(
    Guid Id,
    string Name,
    string? Description,
    int Credits,
    int ValidityDays,
    long PriceMinor,
    string Currency,
    decimal TaxPercent,
    string? TaxLabel,
    long TotalMinor,
    IReadOnlyList<string>? Highlights);

public sealed record CheckoutRequest(Guid PackId, string? IdempotencyKey);

public sealed record CheckoutResponse(Guid OrderId, string CheckoutUrl, DateTimeOffset? ExpiresAt);

/// <summary>Order states as the API names them.</summary>
public static class OrderStatuses
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
    public const string Refunded = "Refunded";
    public const string PartiallyRefunded = "PartiallyRefunded";

    public static readonly IReadOnlyList<string> All = [Pending, Paid, Failed, Expired, Cancelled, Refunded, PartiallyRefunded];

    /// <summary>The payment went through (a later refund does not change that for the "credits arrived" message).</summary>
    public static bool IsPaid(string? status) => status is Paid or Refunded or PartiallyRefunded;

    /// <summary>The payment did not happen and will not happen on this order.</summary>
    public static bool IsFailed(string? status) => status is Failed or Cancelled or Expired;

    /// <summary>Plain-language label for people.</summary>
    public static string Label(string? status) => status switch
    {
        Pending => "Waiting for payment",
        Paid => "Paid",
        Failed => "Payment failed",
        Expired => "Expired",
        Cancelled => "Cancelled",
        Refunded => "Refunded",
        PartiallyRefunded => "Partly refunded",
        _ => string.IsNullOrWhiteSpace(status) ? "Unknown" : status,
    };
}

public sealed record OrderListItemDto(
    Guid Id,
    string? InvoiceNumber,
    string PackName,
    int Credits,
    long TotalMinor,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

public sealed record OrderDto(
    Guid Id,
    string? InvoiceNumber,
    string PackName,
    int Credits,
    int ValidityDays,
    long SubtotalMinor,
    long TaxMinor,
    long TotalMinor,
    decimal TaxPercent,
    string? TaxLabel,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    Guid? LicenseId);

/// <summary>The company details printed on invoices (<c>/client/billing/profile</c>).</summary>
public sealed record BillingProfileDto(
    string? LegalName,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    string? TaxId,
    string? BillingEmail,
    string? RowVersion);

public sealed record SaveBillingProfileRequest(
    string LegalName,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string? State,
    string PostalCode,
    string Country,
    string? TaxId,
    string BillingEmail,
    string? RowVersion);

/// <summary>A pack on the public pricing page (<c>GET /public/packs</c>).</summary>
public sealed record PublicPackDto(
    Guid Id,
    string Name,
    string? Description,
    int Credits,
    int ValidityDays,
    long PriceMinor,
    string Currency,
    string? DisplayPrice,
    IReadOnlyList<string>? Highlights);

// ---- platform staff -------------------------------------------------------------------------------------------------------------

public sealed record AdminPackDto(
    Guid Id,
    string Name,
    string? Description,
    int Credits,
    int ValidityDays,
    long PriceMinor,
    string Currency,
    IReadOnlyList<string>? Highlights,
    int DisplayOrder,
    bool IsActive,
    bool IsPublic,
    string? RowVersion);

public sealed record SaveAdminPackRequest(
    string Name,
    string? Description,
    int Credits,
    int ValidityDays,
    long PriceMinor,
    string Currency,
    IReadOnlyList<string> Highlights,
    int DisplayOrder,
    bool IsActive,
    bool IsPublic,
    string? RowVersion);

public sealed record AdminOrderListItemDto(
    Guid Id,
    string? InvoiceNumber,
    Guid ClientId,
    string? ClientName,
    string PackName,
    int Credits,
    long TotalMinor,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

public sealed record RefundDto(Guid Id, long AmountMinor, int CreditsRevoked, string? Reason, DateTimeOffset CreatedAt);

public sealed record OrderEventDto(string Type, DateTimeOffset At, string? Summary);

public sealed record AdminOrderDto(
    Guid Id,
    string? InvoiceNumber,
    Guid ClientId,
    string? ClientName,
    string PackName,
    int Credits,
    int ValidityDays,
    long SubtotalMinor,
    long TaxMinor,
    long TotalMinor,
    decimal TaxPercent,
    string? TaxLabel,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    Guid? LicenseId,
    string? Provider,
    string? ProviderPaymentId,
    IReadOnlyList<RefundDto>? Refunds,
    IReadOnlyList<OrderEventDto>? Events);

public sealed record RefundOrderRequest(long? AmountMinor, string Reason);

public sealed class AdminOrderQuery
{
    public Guid? ClientId { get; init; }

    public string? Status { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public static class BillingPermissions
{
    public const string Read = "billing.read";
    public const string Manage = "billing.manage";
    public const string PacksManage = "billing.packs.manage";
    public const string OrdersRead = "billing.orders.read";
    public const string Refund = "billing.refund";
}

/// <summary>
/// One logical attempt to buy a pack. The idempotency key stays the same while the same pack is being (re)tried, so a double click, a
/// timeout or a retry can never create two orders; it is replaced once the attempt has a definitive answer or another pack is chosen.
/// </summary>
public sealed class CheckoutAttempt
{
    private string? _key;
    private Guid _pack;

    public string KeyFor(Guid packId)
    {
        if (_key is null || _pack != packId)
        {
            _key = Guid.NewGuid().ToString("N");
            _pack = packId;
        }

        return _key;
    }

    /// <summary>Forget the key: the next send is a new purchase.</summary>
    public void Reset() => _key = null;
}

/// <summary>Decisions about billing errors that several pages share.</summary>
public static class BillingErrors
{
    /// <summary>Online payments are switched off (the API answers 503, or 403 for a user who may otherwise read billing).</summary>
    public static bool IsDisabled(ApiError error) =>
        error.Status is 503 or 403 || error.Code is "BILLING_DISABLED" or "PAYMENTS_DISABLED";

    public const string DisabledMessage = "Online payments are not available yet. Please contact us and we will top up your account for you.";
}

/// <summary>Billing-details form. The rules mirror the API so people see problems inline; the API stays authoritative.</summary>
public sealed class BillingProfileForm
{
    [Required(ErrorMessage = "Enter the name of your company as it should appear on invoices."), StringLength(200)]
    public string LegalName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the first line of your address."), StringLength(200)]
    public string AddressLine1 { get; set; } = string.Empty;

    [StringLength(200)]
    public string? AddressLine2 { get; set; }

    [Required(ErrorMessage = "Enter your city."), StringLength(100)]
    public string City { get; set; } = string.Empty;

    [StringLength(100)]
    public string? State { get; set; }

    [Required(ErrorMessage = "Enter your postcode or ZIP code."), StringLength(20)]
    public string PostalCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your two-letter country code, for example GB."), RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "Use the two-letter country code, for example GB or IN.")]
    public string Country { get; set; } = string.Empty;

    [StringLength(40, ErrorMessage = "A tax number can be at most 40 characters.")]
    public string? TaxId { get; set; }

    [Required(ErrorMessage = "Enter the email address that should receive invoices."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256)]
    public string BillingEmail { get; set; } = string.Empty;

    public string? RowVersion { get; set; }

    public static BillingProfileForm From(BillingProfileDto? profile) => profile is null ? new() : new()
    {
        LegalName = profile.LegalName ?? string.Empty,
        AddressLine1 = profile.AddressLine1 ?? string.Empty,
        AddressLine2 = profile.AddressLine2,
        City = profile.City ?? string.Empty,
        State = profile.State,
        PostalCode = profile.PostalCode ?? string.Empty,
        Country = profile.Country ?? string.Empty,
        TaxId = profile.TaxId,
        BillingEmail = profile.BillingEmail ?? string.Empty,
        RowVersion = profile.RowVersion,
    };

    public SaveBillingProfileRequest ToRequest() => new(
        LegalName.Trim(), AddressLine1.Trim(), AddressLine2.NullIfBlank(), City.Trim(), State.NullIfBlank(), PostalCode.Trim(),
        Country.Trim().ToUpperInvariant(), TaxId.NullIfBlank(), BillingEmail.Trim(), RowVersion);

    /// <summary>A hint for the buy page: the details the invoice needs are missing. The API decides at checkout.</summary>
    public static bool LooksIncomplete(BillingProfileDto? profile) =>
        profile is null || string.IsNullOrWhiteSpace(profile.LegalName) || string.IsNullOrWhiteSpace(profile.AddressLine1)
        || string.IsNullOrWhiteSpace(profile.City) || string.IsNullOrWhiteSpace(profile.Country) || string.IsNullOrWhiteSpace(profile.BillingEmail);
}

/// <summary>Credit-pack editor model (price typed in major units, converted exactly to minor units on save).</summary>
public sealed class PackForm : IValidatableObject
{
    [Required(ErrorMessage = "Give the pack a name."), StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Range(1, 100_000_000, ErrorMessage = "Credits must be at least 1.")]
    public int Credits { get; set; } = 1000;

    [Range(1, 3650, ErrorMessage = "Validity must be between 1 and 3650 days.")]
    public int ValidityDays { get; set; } = 365;

    /// <summary>The price as typed, in major units ("49.00", "1500").</summary>
    public string PriceText { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a currency.")]
    public string Currency { get; set; } = "USD";

    /// <summary>One highlight per line.</summary>
    [StringLength(2000, ErrorMessage = "Highlights are too long.")]
    public string? HighlightsText { get; set; }

    [Range(0, 10_000)]
    public int DisplayOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsPublic { get; set; } = true;

    public string? RowVersion { get; set; }

    public const int MaxHighlights = 8;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var minor = Money.TryParseMajor(PriceText, Currency);
        if (minor is null)
        {
            var decimals = Money.Exponent(Currency);
            yield return new ValidationResult(
                decimals == 0
                    ? $"Enter the price as a whole number of {Currency}, without decimals."
                    : $"Enter the price as a number with at most {decimals} decimals, for example {Money.ToInputText(4900, Currency)}.",
                [nameof(PriceText)]);
        }
        else if (minor <= 0)
        {
            yield return new ValidationResult("The price must be more than zero.", [nameof(PriceText)]);
        }

        if (Highlights().Count > MaxHighlights)
        {
            yield return new ValidationResult($"Use at most {MaxHighlights} highlights.", [nameof(HighlightsText)]);
        }
    }

    public List<string> Highlights() =>
        (HighlightsText ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static PackForm From(AdminPackDto pack) => new()
    {
        Name = pack.Name,
        Description = pack.Description,
        Credits = pack.Credits,
        ValidityDays = pack.ValidityDays,
        PriceText = Money.ToInputText(pack.PriceMinor, pack.Currency),
        Currency = pack.Currency,
        HighlightsText = string.Join('\n', pack.Highlights ?? []),
        DisplayOrder = pack.DisplayOrder,
        IsActive = pack.IsActive,
        IsPublic = pack.IsPublic,
        RowVersion = pack.RowVersion,
    };

    public SaveAdminPackRequest ToRequest() => new(
        Name.Trim(), Description.NullIfBlank(), Credits, ValidityDays, Money.TryParseMajor(PriceText, Currency) ?? 0, Currency.Trim().ToUpperInvariant(),
        Highlights(), DisplayOrder, IsActive, IsPublic, RowVersion);
}
