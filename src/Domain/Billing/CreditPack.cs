using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Billing;

/// <summary>
/// A fixed bundle of credits that a client can buy online (a "top-up"). Global catalogue data, not tenant-owned. The price is
/// stored in minor units (cents, paise) of <see cref="Currency"/> and is the price BEFORE tax; tax comes from <c>Billing:TaxPercent</c>
/// when an order is placed. <see cref="AuditableEntity.IsActive"/> hides a pack from sale without losing the orders that reference it.
/// </summary>
public sealed class CreditPack : AuditableEntity
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int MaxHighlights = 8;
    public const int HighlightMaxLength = 120;
    public const int MaxCredits = 100_000_000;
    public const int MaxValidityDays = 3650;
    public const long MaxPriceMinor = 1_000_000_000_000;

    private CreditPack()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int Credits { get; private set; }

    public int ValidityDays { get; private set; }

    public long PriceMinor { get; private set; }

    /// <summary>ISO 4217 code in upper case.</summary>
    public string Currency { get; private set; } = string.Empty;

    public List<string> Highlights { get; private set; } = [];

    public int DisplayOrder { get; private set; }

    /// <summary>Shown on the public pricing page (inactive packs are never shown anywhere).</summary>
    public bool IsPublic { get; private set; }

    public static CreditPack Create(
        string name, string? description, int credits, int validityDays, long priceMinor, string currency,
        IEnumerable<string>? highlights, int displayOrder, bool isActive, bool isPublic)
    {
        var pack = new CreditPack();
        pack.Apply(name, description, credits, validityDays, priceMinor, currency, highlights, displayOrder, isActive, isPublic);
        return pack;
    }

    public void Update(
        string name, string? description, int credits, int validityDays, long priceMinor, string currency,
        IEnumerable<string>? highlights, int displayOrder, bool isActive, bool isPublic) =>
        Apply(name, description, credits, validityDays, priceMinor, currency, highlights, displayOrder, isActive, isPublic);

    public static bool IsCurrencyCode(string code) => code.Length == 3 && code.All(char.IsAsciiLetterUpper);

    private void Apply(
        string name, string? description, int credits, int validityDays, long priceMinor, string currency,
        IEnumerable<string>? highlights, int displayOrder, bool isActive, bool isPublic)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new DomainException("PACK_NAME_INVALID", "A pack name of at most 100 characters is required.");
        }

        if (description is { Length: > 0 } && description.Trim().Length > DescriptionMaxLength)
        {
            throw new DomainException("PACK_DESCRIPTION_INVALID", "The description is too long.");
        }

        if (credits is < 1 or > MaxCredits)
        {
            throw new DomainException("PACK_CREDITS_INVALID", "Credits must be between 1 and 100,000,000.");
        }

        if (validityDays is < 1 or > MaxValidityDays)
        {
            throw new DomainException("PACK_VALIDITY_INVALID", "Validity must be between 1 and 3650 days.");
        }

        if (priceMinor is < 1 or > MaxPriceMinor)
        {
            throw new DomainException("PACK_PRICE_INVALID", "The price must be positive.");
        }

        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsCurrencyCode(code))
        {
            throw new DomainException("PACK_CURRENCY_INVALID", "The currency must be a 3-letter ISO 4217 code.");
        }

        var list = (highlights ?? []).Select(h => (h ?? string.Empty).Trim()).Where(h => h.Length > 0).ToList();
        if (list.Count > MaxHighlights || list.Any(h => h.Length > HighlightMaxLength))
        {
            throw new DomainException("PACK_HIGHLIGHTS_INVALID", "At most 8 highlights of 120 characters each.");
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Credits = credits;
        ValidityDays = validityDays;
        PriceMinor = priceMinor;
        Currency = code;
        Highlights = list;
        DisplayOrder = displayOrder;
        IsActive = isActive;
        IsPublic = isPublic;
    }
}
