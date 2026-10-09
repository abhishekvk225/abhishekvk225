using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Billing;

/// <summary>
/// Who an invoice is addressed to: one profile per client. A copy of it is frozen into every order when the checkout starts, so a
/// later edit never rewrites an invoice that was already issued.
/// </summary>
public sealed class BillingProfile : AuditableEntity, ITenantOwned
{
    public const int NameMaxLength = 200;
    public const int LineMaxLength = 150;
    public const int ShortMaxLength = 100;
    public const int PostalMaxLength = 20;
    public const int TaxIdMaxLength = 40;
    public const int EmailMaxLength = 256;

    private BillingProfile()
    {
    }

    public Guid ClientId { get; set; }

    public string LegalName { get; private set; } = string.Empty;

    public string AddressLine1 { get; private set; } = string.Empty;

    public string? AddressLine2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string? State { get; private set; }

    public string PostalCode { get; private set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2, upper case.</summary>
    public string Country { get; private set; } = string.Empty;

    /// <summary>GSTIN / VAT number. Letters and digits only, upper case.</summary>
    public string? TaxId { get; private set; }

    public string BillingEmail { get; private set; } = string.Empty;

    public static BillingProfile Create(
        Guid clientId, string? legalName, string? addressLine1, string? addressLine2, string? city, string? state, string? postalCode,
        string? country, string? taxId, string? billingEmail)
    {
        var profile = new BillingProfile { ClientId = clientId };
        profile.Update(legalName, addressLine1, addressLine2, city, state, postalCode, country, taxId, billingEmail);
        return profile;
    }

    public void Update(
        string? legalName, string? addressLine1, string? addressLine2, string? city, string? state, string? postalCode,
        string? country, string? taxId, string? billingEmail)
    {
        var countryCode = (country ?? string.Empty).Trim().ToUpperInvariant();
        if (countryCode.Length > 0 && (countryCode.Length != 2 || !countryCode.All(char.IsAsciiLetterUpper)))
        {
            throw new DomainException("BILLING_COUNTRY_INVALID", "The country must be a 2-letter ISO code.");
        }

        var tax = new string((taxId ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        if (tax.Length > TaxIdMaxLength || !tax.All(char.IsAsciiLetterOrDigit))
        {
            throw new DomainException("BILLING_TAXID_INVALID", "The tax id may contain letters and digits only (at most 40).");
        }

        LegalName = Limit(legalName, NameMaxLength, "legal name");
        AddressLine1 = Limit(addressLine1, LineMaxLength, "address");
        AddressLine2 = NullIfEmpty(Limit(addressLine2, LineMaxLength, "address"));
        City = Limit(city, ShortMaxLength, "city");
        State = NullIfEmpty(Limit(state, ShortMaxLength, "state"));
        PostalCode = Limit(postalCode, PostalMaxLength, "postal code");
        Country = countryCode;
        TaxId = NullIfEmpty(tax);
        BillingEmail = Limit(billingEmail, EmailMaxLength, "email");
    }

    /// <summary>Everything an invoice needs is present (the tax id only when the platform requires one).</summary>
    public bool IsComplete(bool requireTaxId) =>
        LegalName.Length > 0 && AddressLine1.Length > 0 && City.Length > 0 && PostalCode.Length > 0 && Country.Length == 2
        && BillingEmail.Length > 0 && (!requireTaxId || !string.IsNullOrEmpty(TaxId));

    private static string Limit(string? value, int max, string what)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length > max)
        {
            throw new DomainException("BILLING_FIELD_TOO_LONG", $"The {what} is too long (at most {max} characters).");
        }

        return text;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
