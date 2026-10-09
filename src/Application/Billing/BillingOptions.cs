using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using NexaVerify.Domain.Billing;

namespace NexaVerify.Application.Billing;

/// <summary>Names of the payment providers (<c>Billing:Provider</c>). Matching is case-insensitive.</summary>
public static class PaymentProviders
{
    public const string None = "None";
    public const string Stripe = "Stripe";
    public const string Razorpay = "Razorpay";
    public const string Simulated = "Simulated";

    public static bool IsKnown(string? name) =>
        string.Equals(name, Stripe, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Razorpay, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, Simulated, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Online payments for credit packs (<c>Billing:*</c>, deploy/CONFIG.md). Off unless explicitly enabled. Keys are secrets: use the secrets store.</summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public bool Enabled { get; set; }

    /// <summary><c>None</c>, <c>Stripe</c>, <c>Razorpay</c> or <c>Simulated</c> (Development and Testing environments only).</summary>
    public string Provider { get; set; } = PaymentProviders.None;

    /// <summary>Tax added on top of every pack price (for example 18 for GST).</summary>
    [Range(0, 100)]
    public decimal TaxPercent { get; set; }

    [Required]
    [StringLength(30, MinimumLength = 1)]
    public string TaxLabel { get; set; } = "Tax";

    /// <summary>ISO 4217 codes a pack may be priced in; packs in other currencies are neither listed nor sold.</summary>
    public string[] AllowedCurrencies { get; set; } = ["INR", "USD"];

    /// <summary>A checkout needs a complete billing profile (name, address, billing email).</summary>
    public bool RequireBillingProfile { get; set; } = true;

    /// <summary>The profile must also carry a tax id (GSTIN / VAT number).</summary>
    public bool RequireTaxId { get; set; }

    /// <summary>How long the hosted payment page stays open.</summary>
    [Range(30, 1440)]
    public int CheckoutExpiryMinutes { get; set; } = 60;

    /// <summary>A Pending order older than this is expired by the nightly job (after a last look at the provider).</summary>
    [Range(1, 168)]
    public int PendingExpiryHours { get; set; } = 24;

    /// <summary>The job asks the provider about Pending orders older than this.</summary>
    [Range(1, 1440)]
    public int ReconcileAfterMinutes { get; set; } = 15;

    /// <summary>The order page asks the provider (at most this often per order) when the webhook is late. 0 = never.</summary>
    [Range(0, 3600)]
    public int InlineReconcileSeconds { get; set; } = 15;

    [Range(1, 1000)]
    public int MaxCheckoutsPerUserPerHour { get; set; } = 10;

    [Range(1, 10_000)]
    public int MaxCheckoutsPerClientPerHour { get; set; } = 30;

    /// <summary>Unfinished orders (Pending, not yet expired) one client may have at the same time.</summary>
    [Range(1, 100)]
    public int MaxOpenOrdersPerClient { get; set; } = 5;

    [Range(1024, 1_048_576)]
    public int WebhookMaxBodyBytes { get; set; } = 65_536;

    [Range(1, 1440)]
    public int JobIntervalMinutes { get; set; } = 60;

    [Range(0, 3600)]
    public int JobInitialDelaySeconds { get; set; } = 120;

    [Range(1, 1000)]
    public int JobBatchSize { get; set; } = 100;

    /// <summary>Where the provider sends the customer after paying / cancelling, relative to <c>Portal:PublicBaseUrl</c>. <c>{orderId}</c> is replaced. Never trusted as proof of payment.</summary>
    [Required]
    public string SuccessPath { get; set; } = "/billing/success?order={orderId}";

    [Required]
    public string CancelPath { get; set; } = "/billing/cancelled?order={orderId}";

    public StripeOptions Stripe { get; set; } = new();

    public RazorpayOptions Razorpay { get; set; } = new();

    public SellerOptions Seller { get; set; } = new();

    public bool IsProvider(string name) => string.Equals(Provider, name, StringComparison.OrdinalIgnoreCase);

    public bool AllowsCurrency(string currency) => AllowedCurrencies.Contains(currency, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>sandbox</c>, <c>live</c>, <c>simulated</c> or <c>none</c>, read from the shape of the configured key. Shown to platform staff only.</summary>
    public string Mode()
    {
        if (IsProvider(PaymentProviders.Simulated))
        {
            return "simulated";
        }

        if (IsProvider(PaymentProviders.Stripe))
        {
            return Stripe.SecretKey switch
            {
                { } k when k.Contains("_live_", StringComparison.Ordinal) => "live",
                { } k when k.Contains("_test_", StringComparison.Ordinal) => "sandbox",
                _ => "none",
            };
        }

        if (IsProvider(PaymentProviders.Razorpay))
        {
            return Razorpay.KeyId switch
            {
                { } k when k.StartsWith("rzp_live_", StringComparison.Ordinal) => "live",
                { } k when k.StartsWith("rzp_test_", StringComparison.Ordinal) => "sandbox",
                _ => "none",
            };
        }

        return "none";
    }
}

public sealed class StripeOptions
{
    /// <summary>Secret API key (<c>sk_...</c> or a restricted <c>rk_...</c> key). A secret.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Signing secret of the webhook endpoint (<c>whsec_...</c>). A secret.</summary>
    public string? WebhookSecret { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";

    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>How far a webhook timestamp may be from now (replay protection).</summary>
    [Range(30, 3600)]
    public int ToleranceSeconds { get; set; } = 300;
}

public sealed class RazorpayOptions
{
    public string? KeyId { get; set; }

    /// <summary>API key secret. A secret.</summary>
    public string? KeySecret { get; set; }

    /// <summary>Secret configured on the webhook in the Razorpay dashboard. A secret.</summary>
    public string? WebhookSecret { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.razorpay.com";

    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>The seller printed on invoices.</summary>
public sealed class SellerOptions
{
    [StringLength(200)]
    public string LegalName { get; set; } = "NexaVerify";

    public string[] AddressLines { get; set; } = [];

    /// <summary>Seller's tax registration (GSTIN / VAT number).</summary>
    [StringLength(40)]
    public string? TaxId { get; set; }

    [StringLength(30)]
    public string TaxIdLabel { get; set; } = "Tax ID";

    [StringLength(256)]
    public string? Email { get; set; }

    [StringLength(500)]
    public string? InvoiceFooter { get; set; }
}

public sealed class BillingOptionsValidator : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions options)
    {
        var problems = new List<string>();
        if (options.AllowedCurrencies.Length == 0 || options.AllowedCurrencies.Any(c => !CreditPack.IsCurrencyCode((c ?? string.Empty).ToUpperInvariant())))
        {
            problems.Add("Billing:AllowedCurrencies must list ISO 4217 codes such as INR or USD.");
        }

        if (options.Provider is not null && !PaymentProviders.IsKnown(options.Provider) && !options.IsProvider(PaymentProviders.None))
        {
            problems.Add("Billing:Provider must be None, Stripe, Razorpay or Simulated.");
        }

        if (!options.SuccessPath.StartsWith('/') || !options.CancelPath.StartsWith('/'))
        {
            problems.Add("Billing:SuccessPath and Billing:CancelPath must start with '/'.");
        }

        if (options.Enabled)
        {
            if (!PaymentProviders.IsKnown(options.Provider))
            {
                problems.Add("Billing:Enabled needs Billing:Provider to be Stripe, Razorpay or Simulated.");
            }

            if (options.IsProvider(PaymentProviders.Stripe))
            {
                Require(problems, options.Stripe.SecretKey, "Billing:Stripe:SecretKey");
                Require(problems, options.Stripe.WebhookSecret, "Billing:Stripe:WebhookSecret");
                RequireHttps(problems, options.Stripe.ApiBaseUrl, "Billing:Stripe:ApiBaseUrl");
            }

            if (options.IsProvider(PaymentProviders.Razorpay))
            {
                Require(problems, options.Razorpay.KeyId, "Billing:Razorpay:KeyId");
                Require(problems, options.Razorpay.KeySecret, "Billing:Razorpay:KeySecret");
                Require(problems, options.Razorpay.WebhookSecret, "Billing:Razorpay:WebhookSecret");
                RequireHttps(problems, options.Razorpay.ApiBaseUrl, "Billing:Razorpay:ApiBaseUrl");
            }

            if (string.IsNullOrWhiteSpace(options.Seller.LegalName))
            {
                problems.Add("Billing:Seller:LegalName is required when billing is enabled (it is printed on invoices).");
            }
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    private static void Require(List<string> problems, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{key} is required for the configured provider.");
        }
    }

    private static void RequireHttps(List<string> problems, string value, string key)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"{key} must be an absolute https URL.");
        }
    }
}
