using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using NexaVerify.Contracts.Billing;
using NexaVerify.Domain.Billing;

namespace NexaVerify.Application.Billing;

/// <summary>Minor-unit arithmetic and display. Prices are whole numbers of the currency's smallest unit; only formatting knows about exponents.</summary>
public static class Money
{
    private static readonly Dictionary<string, int> Exponents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPY"] = 0, ["KRW"] = 0, ["VND"] = 0, ["CLP"] = 0, ["ISK"] = 0, ["UGX"] = 0, ["XAF"] = 0, ["XOF"] = 0,
        ["BHD"] = 3, ["JOD"] = 3, ["KWD"] = 3, ["OMR"] = 3, ["TND"] = 3,
    };

    public static int Exponent(string currency) => Exponents.TryGetValue(currency, out var e) ? e : 2;

    /// <summary>For example <c>INR 1,180.00</c> (invariant culture: invoices must read the same on every server).</summary>
    public static string Format(long minor, string currency)
    {
        var exponent = Exponent(currency);
        var value = (decimal)minor / (decimal)Math.Pow(10, exponent);
        return currency.ToUpperInvariant() + " " + value.ToString("N" + exponent.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}

/// <summary>The "bill to" frozen into an order at checkout time (so a later profile edit never rewrites an issued invoice).</summary>
public sealed record BuyerSnapshot(
    string LegalName, string AddressLine1, string? AddressLine2, string City, string? State, string PostalCode, string Country, string? TaxId, string BillingEmail)
{
    public static BuyerSnapshot Empty { get; } = new(string.Empty, string.Empty, null, string.Empty, null, string.Empty, string.Empty, null, string.Empty);

    public static BuyerSnapshot From(BillingProfile? profile) => profile is null
        ? Empty
        : new BuyerSnapshot(profile.LegalName, profile.AddressLine1, profile.AddressLine2, profile.City, profile.State, profile.PostalCode, profile.Country, profile.TaxId, profile.BillingEmail);

    public string ToJson() => JsonSerializer.Serialize(this);

    public static BuyerSnapshot Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BuyerSnapshot>(json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}

/// <summary>A printable invoice. <c>Html</c> is a complete, self-contained page with every dynamic value HTML-encoded.</summary>
public sealed record InvoiceDocument(string FileName, string Html);

/// <summary>Renders the invoice page. Pure: the same order always produces the same page, and nothing in it is read from the request.</summary>
public static class InvoiceRenderer
{
    public static InvoiceDocument Render(PaymentOrder order, string clientName, IReadOnlyList<Refund> refunds, SellerOptions seller)
    {
        var buyer = BuyerSnapshot.Parse(order.BuyerJson);
        var number = order.InvoiceNumber ?? order.Id.ToString("D");
        var sb = new StringBuilder(4096);

        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        static string Date(DateTime? value) => value is { } d ? d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : string.Empty;

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>Invoice ").Append(E(number)).Append("</title><style>")
            .Append("body{font-family:Arial,Helvetica,sans-serif;color:#111;margin:0;padding:32px;font-size:14px;line-height:1.45}")
            .Append(".wrap{max-width:780px;margin:0 auto}h1{font-size:26px;margin:0 0 4px}.muted{color:#555}")
            .Append(".row{display:flex;justify-content:space-between;gap:24px;margin:24px 0}.row>div{flex:1}")
            .Append("table{width:100%;border-collapse:collapse;margin-top:16px}th,td{padding:8px 6px;border-bottom:1px solid #ddd;text-align:left}")
            .Append("th.n,td.n{text-align:right}tfoot td{border-bottom:0}.total td{font-weight:bold;border-top:2px solid #111}")
            .Append(".status{display:inline-block;border:1px solid #111;padding:2px 8px;border-radius:4px;font-size:12px;text-transform:uppercase}")
            .Append("@media print{body{padding:0}}</style></head><body><div class=\"wrap\">");

        sb.Append("<h1>Tax invoice</h1><div class=\"muted\">").Append(E(number)).Append("</div>");
        sb.Append("<div class=\"row\"><div><strong>").Append(E(seller.LegalName)).Append("</strong><br>");
        foreach (var line in seller.AddressLines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            sb.Append(E(line)).Append("<br>");
        }

        if (!string.IsNullOrWhiteSpace(seller.TaxId))
        {
            sb.Append(E(seller.TaxIdLabel)).Append(": ").Append(E(seller.TaxId)).Append("<br>");
        }

        if (!string.IsNullOrWhiteSpace(seller.Email))
        {
            sb.Append(E(seller.Email));
        }

        sb.Append("</div><div><strong>Bill to</strong><br>");
        sb.Append(E(string.IsNullOrWhiteSpace(buyer.LegalName) ? clientName : buyer.LegalName)).Append("<br>");
        foreach (var line in new[] { buyer.AddressLine1, buyer.AddressLine2, JoinNonEmpty(", ", buyer.City, buyer.State, buyer.PostalCode), buyer.Country })
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.Append(E(line)).Append("<br>");
            }
        }

        if (!string.IsNullOrWhiteSpace(buyer.TaxId))
        {
            sb.Append("Tax ID: ").Append(E(buyer.TaxId)).Append("<br>");
        }

        if (!string.IsNullOrWhiteSpace(buyer.BillingEmail))
        {
            sb.Append(E(buyer.BillingEmail));
        }

        sb.Append("</div><div><strong>Invoice date</strong><br>").Append(E(Date(order.PaidAt ?? order.CreatedAt)))
            .Append("<br><strong>Order</strong><br>").Append(E(order.Id.ToString("D")))
            .Append("<br><span class=\"status\">").Append(E(order.Status.ToString())).Append("</span></div></div>");

        var currency = order.Currency;
        sb.Append("<table><thead><tr><th>Description</th><th class=\"n\">Credits</th><th class=\"n\">Amount</th></tr></thead><tbody>");
        sb.Append("<tr><td>").Append(E(order.PackName)).Append("<div class=\"muted\">Credits valid for ").Append(order.ValidityDays.ToString(CultureInfo.InvariantCulture))
            .Append(" days from purchase</div></td><td class=\"n\">").Append(order.Credits.ToString("N0", CultureInfo.InvariantCulture))
            .Append("</td><td class=\"n\">").Append(E(Money.Format(order.SubtotalMinor, currency))).Append("</td></tr></tbody><tfoot>");
        sb.Append("<tr><td colspan=\"2\" class=\"n\">Subtotal</td><td class=\"n\">").Append(E(Money.Format(order.SubtotalMinor, currency))).Append("</td></tr>");
        sb.Append("<tr><td colspan=\"2\" class=\"n\">").Append(E(order.TaxLabel)).Append(' ')
            .Append(order.TaxPercent.ToString("0.##", CultureInfo.InvariantCulture)).Append("%</td><td class=\"n\">")
            .Append(E(Money.Format(order.TaxMinor, currency))).Append("</td></tr>");
        sb.Append("<tr class=\"total\"><td colspan=\"2\" class=\"n\">Total</td><td class=\"n\">").Append(E(Money.Format(order.TotalMinor, currency))).Append("</td></tr>");

        foreach (var refund in refunds.Where(r => r.Status == RefundStatus.Succeeded))
        {
            sb.Append("<tr><td colspan=\"2\" class=\"n\">Refunded ").Append(E(Date(refund.CompletedAt))).Append("</td><td class=\"n\">-")
                .Append(E(Money.Format(refund.AmountMinor, currency))).Append("</td></tr>");
        }

        sb.Append("</tfoot></table>");
        if (!string.IsNullOrWhiteSpace(seller.InvoiceFooter))
        {
            sb.Append("<p class=\"muted\">").Append(E(seller.InvoiceFooter)).Append("</p>");
        }

        sb.Append("</div></body></html>");
        var safeName = new string(number.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return new InvoiceDocument($"invoice-{safeName}.html", sb.ToString());
    }

    private static string JoinNonEmpty(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

internal static class BillingMapping
{
    public static CreditPackDto ToClientDto(this CreditPack pack, BillingOptions options)
    {
        var tax = PaymentOrder.TaxFor(pack.PriceMinor, options.TaxPercent);
        return new CreditPackDto(
            pack.Id, pack.Name, pack.Description, pack.Credits, pack.ValidityDays, pack.PriceMinor, pack.Currency,
            options.TaxPercent, options.TaxLabel, tax, pack.PriceMinor + tax, pack.Highlights.ToList());
    }

    public static PublicPackDto ToPublicDto(this CreditPack pack, BillingOptions options)
    {
        var tax = PaymentOrder.TaxFor(pack.PriceMinor, options.TaxPercent);
        return new PublicPackDto(
            pack.Id, pack.Name, pack.Description, pack.Credits, pack.ValidityDays, pack.PriceMinor, pack.Currency,
            options.TaxPercent, options.TaxLabel, pack.PriceMinor + tax, pack.Highlights.ToList());
    }

    public static AdminCreditPackDto ToAdminDto(this CreditPack pack) => new(
        pack.Id, pack.Name, pack.Description, pack.Credits, pack.ValidityDays, pack.PriceMinor, pack.Currency, pack.Highlights.ToList(),
        pack.DisplayOrder, pack.IsActive, pack.IsPublic, pack.CreatedAt, pack.UpdatedAt);

    public static OrderDto ToDto(this PaymentOrder o) => new(
        o.Id, o.InvoiceNumber, o.PackId, o.PackName, o.Credits, o.ValidityDays, o.SubtotalMinor, o.TaxMinor, o.TotalMinor, o.TaxPercent, o.TaxLabel,
        o.Currency, o.Status.ToString(), o.CreatedAt, o.PaidAt, o.ExpiresAt, o.LicenseId, o.RefundedMinor);

    public static OrderListItemDto ToListItem(this PaymentOrder o) => new(
        o.Id, o.InvoiceNumber, o.PackName, o.Credits, o.TotalMinor, o.Currency, o.Status.ToString(), o.CreatedAt, o.PaidAt);

    public static AdminOrderListItemDto ToAdminListItem(this OrderRow row) => new(
        row.Order.Id, row.Order.ClientId, row.ClientName, row.Order.InvoiceNumber, row.Order.PackName, row.Order.Credits, row.Order.TotalMinor,
        row.Order.RefundedMinor, row.Order.Currency, row.Order.Status.ToString(), row.Order.Provider, row.Order.CreatedAt, row.Order.PaidAt);

    public static RefundDto ToDto(this Refund r) => new(
        r.Id, r.AmountMinor, r.CreditsRevoked, r.Reason, r.Status.ToString(), r.ProviderRefundId, r.CreatedBy, r.CreatedAt);

    public static BillingProfileDto ToDto(this BillingProfile? p, bool requireTaxId) => p is null
        ? new BillingProfileDto(string.Empty, string.Empty, null, string.Empty, null, string.Empty, string.Empty, null, string.Empty, false)
        : new BillingProfileDto(p.LegalName, p.AddressLine1, p.AddressLine2, p.City, p.State, p.PostalCode, p.Country, p.TaxId, p.BillingEmail, p.IsComplete(requireTaxId));
}
