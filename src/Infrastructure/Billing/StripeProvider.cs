using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Billing;

namespace NexaVerify.Infrastructure.Billing;

/// <summary>
/// Stripe Checkout (hosted page) over plain HTTPS, no SDK. NOT verified against the live Stripe API from this repository: the
/// request/response shapes and the signature scheme follow Stripe's documentation and are covered by fixture and known-vector tests.
/// Card data never touches NexaVerify: the customer pays on the page behind <see cref="CheckoutSession.CheckoutUrl"/>.
/// </summary>
public sealed class StripeProvider : IPaymentProvider
{
    public const string ProviderName = "stripe";

    private readonly HttpClient _http;
    private readonly StripeOptions _options;
    private readonly TimeProvider _time;

    public StripeProvider(HttpClient http, IOptions<BillingOptions> options, TimeProvider time)
    {
        _http = http;
        _options = options.Value.Stripe;
        _time = time;
    }

    public string Name => ProviderName;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutOrder order, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // Stripe accepts a session lifetime of 30 minutes to 24 hours.
        var expires = order.ExpiresAt;
        expires = expires < now.AddMinutes(30) ? now.AddMinutes(30) : expires > now.AddHours(24) ? now.AddHours(24) : expires;

        var currency = order.Currency.ToLowerInvariant();
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("success_url", successUrl.AbsoluteUri),
            new("cancel_url", cancelUrl.AbsoluteUri),
            new("client_reference_id", order.OrderId.ToString("D")),
            new("metadata[orderId]", order.OrderId.ToString("D")),
            new("payment_intent_data[metadata][orderId]", order.OrderId.ToString("D")),
            new("expires_at", ProviderJson.Invariant(ProviderJson.ToUnix(expires))),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", currency),
            new("line_items[0][price_data][unit_amount]", ProviderJson.Invariant(order.SubtotalMinor)),
            new("line_items[0][price_data][product_data][name]", $"{order.PackName} ({order.Credits.ToString("N0", CultureInfo.InvariantCulture)} credits)"),
        };
        if (order.TaxMinor > 0)
        {
            form.Add(new("line_items[1][quantity]", "1"));
            form.Add(new("line_items[1][price_data][currency]", currency));
            form.Add(new("line_items[1][price_data][unit_amount]", ProviderJson.Invariant(order.TaxMinor)));
            form.Add(new("line_items[1][price_data][product_data][name]", $"{order.TaxLabel} {order.TaxPercent.ToString("0.##", CultureInfo.InvariantCulture)}%"));
        }

        if (!string.IsNullOrWhiteSpace(order.CustomerEmail))
        {
            form.Add(new("customer_email", order.CustomerEmail));
        }

        using var request = Request(HttpMethod.Post, "v1/checkout/sessions", form);
        request.Headers.Add("Idempotency-Key", "nexa-checkout-" + order.OrderId.ToString("N"));
        using var doc = await ProviderHttp.SendAsync(_http, request, "Stripe", cancellationToken);
        var root = doc.RootElement;
        var id = root.At("id").Text();
        var url = root.At("url").Text();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PaymentProviderException("Stripe did not return a checkout page.");
        }

        return new CheckoutSession(id, url, root.At("expires_at").Number() is { } unix ? ProviderJson.FromUnix(unix) : expires);
    }

    public WebhookParseResult VerifyAndParseWebhook(IReadOnlyDictionary<string, string> headers, byte[] rawBody)
    {
        // 1. Authenticate the RAW bytes before reading anything from them.
        if (string.IsNullOrEmpty(_options.WebhookSecret)
            || !headers.TryGetValue("Stripe-Signature", out var header)
            || !TryParseHeader(header, out var timestamp, out var signatures))
        {
            return WebhookParseResult.Invalid;
        }

        var age = _time.GetUtcNow().ToUnixTimeSeconds() - timestamp;
        if (Math.Abs(age) > _options.ToleranceSeconds)
        {
            return WebhookParseResult.Invalid; // stale (or from the future): a captured request can not be replayed later
        }

        var prefix = Encoding.UTF8.GetBytes(ProviderJson.Invariant(timestamp) + ".");
        var signed = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signed, 0);
        rawBody.CopyTo(signed, prefix.Length);
        var expected = WebhookSignatures.Hmac(_options.WebhookSecret, signed);

        var valid = false;
        foreach (var candidate in signatures)
        {
            valid |= WebhookSignatures.HexMatches(candidate, expected); // no early exit: same work whichever v1 matches
        }

        if (!valid)
        {
            return WebhookParseResult.Invalid;
        }

        // 2. Only now parse.
        try
        {
            using var doc = JsonDocument.Parse(rawBody, ProviderJson.Options);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return WebhookParseResult.Unreadable;
        }
    }

    public async Task<ProviderRefund> RefundAsync(
        string providerPaymentId, long amountMinor, string currency, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", providerPaymentId),
            new("amount", ProviderJson.Invariant(amountMinor)),
            new("reason", "requested_by_customer"),
            new("metadata[refundId]", idempotencyKey),
        };
        using var request = Request(HttpMethod.Post, "v1/refunds", form);
        request.Headers.Add("Idempotency-Key", "nexa-refund-" + idempotencyKey);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Stripe", cancellationToken);
        var id = doc.RootElement.At("id").Text();
        var status = doc.RootElement.At("status").Text();
        if (string.IsNullOrEmpty(id) || status is "failed" or "canceled")
        {
            throw new PaymentProviderException("Stripe did not accept the refund.");
        }

        return new ProviderRefund(id);
    }

    public async Task<ProviderPaymentState> FetchPaymentAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "v1/checkout/sessions/" + Uri.EscapeDataString(sessionId), null);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Stripe", cancellationToken);
        var root = doc.RootElement;
        var paymentStatus = root.At("payment_status").Text();
        var status = root.At("status").Text();
        if (paymentStatus == "paid")
        {
            return new ProviderPaymentState(
                ProviderPaymentStatus.Paid, root.At("payment_intent").Text(), root.At("amount_total").Number(), root.At("currency").Text()?.ToUpperInvariant());
        }

        return new ProviderPaymentState(status == "expired" ? ProviderPaymentStatus.Expired : ProviderPaymentStatus.Pending, null, null, null);
    }

    public async Task CancelCheckoutAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "v1/checkout/sessions/" + Uri.EscapeDataString(sessionId) + "/expire", []);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Stripe", cancellationToken);
    }

    /// <summary>Maps a verified Stripe event. Public for the fixture tests.</summary>
    public static WebhookParseResult Parse(JsonElement root)
    {
        var eventId = root.At("id").Text();
        var type = root.At("type").Text();
        var obj = root.At("data", "object");
        if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(type) || obj is not { } data)
        {
            return WebhookParseResult.Unreadable;
        }

        var orderId = data.At("client_reference_id").Id() ?? data.At("metadata", "orderId").Id();
        switch (type)
        {
            case "checkout.session.completed" or "checkout.session.async_payment_succeeded":
                // A completed session of a delayed method (bank debit) is not yet paid: wait for async_payment_succeeded.
                if (data.At("payment_status").Text() != "paid")
                {
                    return WebhookParseResult.NotRelevant;
                }

                return WebhookParseResult.Of(new NormalisedPaymentEvent(
                    ProviderName, eventId, PaymentEventType.PaymentSucceeded, orderId, data.At("payment_intent").Text(),
                    data.At("amount_total").Number(), data.At("currency").Text()?.ToUpperInvariant()));

            case "checkout.session.async_payment_failed":
                return WebhookParseResult.Of(new NormalisedPaymentEvent(ProviderName, eventId, PaymentEventType.PaymentFailed, orderId, null, null, null));

            case "checkout.session.expired":
                return WebhookParseResult.Of(new NormalisedPaymentEvent(ProviderName, eventId, PaymentEventType.CheckoutExpired, orderId, null, null, null));

            case "refund.created":
                return WebhookParseResult.Of(new NormalisedPaymentEvent(
                    ProviderName, eventId, PaymentEventType.RefundCreated, data.At("metadata", "orderId").Id(), data.At("payment_intent").Text(),
                    data.At("amount").Number(), data.At("currency").Text()?.ToUpperInvariant(), data.At("id").Text()));

            default:
                return WebhookParseResult.NotRelevant;
        }
    }

    /// <summary><c>t=1492774577,v1=5257a8...,v1=...,v0=...</c>. Unknown schemes (v0) are ignored; a header without t or v1 is invalid.</summary>
    private static bool TryParseHeader(string header, out long timestamp, out List<string> signatures)
    {
        timestamp = 0;
        signatures = [];
        var haveTimestamp = false;
        foreach (var part in header.Split(','))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t" && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
                haveTimestamp = true;
            }
            else if (pair[0] == "v1")
            {
                signatures.Add(pair[1]);
            }
        }

        return haveTimestamp && signatures.Count > 0 && signatures.Count <= 10;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, IReadOnlyCollection<KeyValuePair<string, string>>? form)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        request.Headers.Add("Stripe-Version", "2024-06-20");
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        return request;
    }
}
