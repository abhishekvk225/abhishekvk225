using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Billing;

namespace NexaVerify.Infrastructure.Billing;

/// <summary>
/// Razorpay hosted checkout through Payment Links (a hosted page, no JavaScript on our side), over plain HTTPS, no SDK. NOT verified
/// against the live Razorpay API from this repository: the shapes follow Razorpay's documentation and are covered by fixture and
/// known-vector tests. Razorpay's webhook signature is an HMAC-SHA256 of the raw body without a timestamp, so replay protection rests on
/// the event ledger (<c>X-Razorpay-Event-Id</c>). A payment link has no "cancel" redirect: <c>cancelUrl</c> is not used.
/// </summary>
public sealed class RazorpayProvider : IPaymentProvider
{
    public const string ProviderName = "razorpay";

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly RazorpayOptions _options;
    private readonly TimeProvider _time;

    public RazorpayProvider(HttpClient http, IOptions<BillingOptions> options, TimeProvider time)
    {
        _http = http;
        _options = options.Value.Razorpay;
        _time = time;
    }

    public string Name => ProviderName;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutOrder order, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = order.ExpiresAt < now.AddMinutes(16) ? now.AddMinutes(16) : order.ExpiresAt; // Razorpay needs at least 15 minutes

        var body = new Dictionary<string, object?>
        {
            ["amount"] = order.TotalMinor,
            ["currency"] = order.Currency.ToUpperInvariant(),
            ["accept_partial"] = false,
            ["reference_id"] = order.OrderId.ToString("D"),
            ["description"] = Truncate($"{order.PackName} - {order.Credits.ToString("N0", CultureInfo.InvariantCulture)} credits", 2000),
            ["notify"] = new { sms = false, email = false },
            ["reminder_enable"] = false,
            ["notes"] = new { orderId = order.OrderId.ToString("D") },
            ["callback_url"] = successUrl.AbsoluteUri,
            ["callback_method"] = "get",
            ["expire_by"] = ProviderJson.ToUnix(expires),
        };
        if (!string.IsNullOrWhiteSpace(order.CustomerEmail) || !string.IsNullOrWhiteSpace(order.CustomerName))
        {
            body["customer"] = new { name = order.CustomerName, email = order.CustomerEmail };
        }

        using var request = Request(HttpMethod.Post, "v1/payment_links", body);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Razorpay", cancellationToken);
        var id = doc.RootElement.At("id").Text();
        var url = doc.RootElement.At("short_url").Text();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PaymentProviderException("Razorpay did not return a payment link.");
        }

        return new CheckoutSession(id, url, doc.RootElement.At("expire_by").Number() is { } unix and > 0 ? ProviderJson.FromUnix(unix) : expires);
    }

    public WebhookParseResult VerifyAndParseWebhook(IReadOnlyDictionary<string, string> headers, byte[] rawBody)
    {
        // 1. Authenticate the RAW bytes before reading anything from them.
        if (string.IsNullOrEmpty(_options.WebhookSecret) || !headers.TryGetValue("X-Razorpay-Signature", out var signature))
        {
            return WebhookParseResult.Invalid;
        }

        if (!WebhookSignatures.HexMatches(signature, WebhookSignatures.Hmac(_options.WebhookSecret, rawBody)))
        {
            return WebhookParseResult.Invalid;
        }

        // 2. Only now parse. Razorpay's own event id is the idempotency key; without one the digest of the (authenticated) body stands in.
        var eventId = headers.TryGetValue("X-Razorpay-Event-Id", out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.Trim()
            : "body-" + Convert.ToHexStringLower(SHA256.HashData(rawBody))[..40];
        try
        {
            using var doc = JsonDocument.Parse(rawBody, ProviderJson.Options);
            return Parse(doc.RootElement, eventId);
        }
        catch (JsonException)
        {
            return WebhookParseResult.Unreadable;
        }
    }

    public async Task<ProviderRefund> RefundAsync(
        string providerPaymentId, long amountMinor, string currency, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["amount"] = amountMinor,
            ["speed"] = "normal",
            ["receipt"] = idempotencyKey,
            ["notes"] = new { refundId = idempotencyKey },
        };
        using var request = Request(HttpMethod.Post, "v1/payments/" + Uri.EscapeDataString(providerPaymentId) + "/refund", body);
        request.Headers.Add("X-Refund-Idempotency", idempotencyKey);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Razorpay", cancellationToken);
        var id = doc.RootElement.At("id").Text();
        if (string.IsNullOrEmpty(id) || doc.RootElement.At("status").Text() == "failed")
        {
            throw new PaymentProviderException("Razorpay did not accept the refund.");
        }

        return new ProviderRefund(id);
    }

    public async Task<ProviderPaymentState> FetchPaymentAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "v1/payment_links/" + Uri.EscapeDataString(sessionId), null);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Razorpay", cancellationToken);
        var root = doc.RootElement;
        switch (root.At("status").Text())
        {
            case "paid":
                string? paymentId = null;
                long? amount = null;
                if (root.At("payments") is { ValueKind: JsonValueKind.Array } payments)
                {
                    foreach (var payment in payments.EnumerateArray())
                    {
                        if (payment.At("status").Text() is "captured" or "authorized" or null && payment.At("payment_id").Text() is { } id)
                        {
                            paymentId = id;
                            amount = payment.At("amount").Number();
                            break;
                        }
                    }
                }

                return new ProviderPaymentState(ProviderPaymentStatus.Paid, paymentId, amount ?? root.At("amount_paid").Number(), root.At("currency").Text()?.ToUpperInvariant());
            case "expired" or "cancelled":
                return new ProviderPaymentState(ProviderPaymentStatus.Expired, null, null, null);
            default:
                return new ProviderPaymentState(ProviderPaymentStatus.Pending, null, null, null);
        }
    }

    public async Task CancelCheckoutAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "v1/payment_links/" + Uri.EscapeDataString(sessionId) + "/cancel", null);
        using var doc = await ProviderHttp.SendAsync(_http, request, "Razorpay", cancellationToken);
    }

    /// <summary>Maps a verified Razorpay event. Public for the fixture tests.</summary>
    public static WebhookParseResult Parse(JsonElement root, string eventId)
    {
        var type = root.At("event").Text();
        if (string.IsNullOrEmpty(type))
        {
            return WebhookParseResult.Unreadable;
        }

        var link = root.At("payload", "payment_link", "entity");
        var payment = root.At("payload", "payment", "entity");
        switch (type)
        {
            case "payment_link.paid":
                {
                    var orderId = link.Id(x => x.At("reference_id")) ?? link.Id(x => x.At("notes", "orderId"));
                    var amount = payment.Number(x => x.At("amount")) ?? link.Number(x => x.At("amount_paid"));
                    var currency = payment.Text(x => x.At("currency")) ?? link.Text(x => x.At("currency"));
                    return WebhookParseResult.Of(new NormalisedPaymentEvent(
                        ProviderName, eventId, PaymentEventType.PaymentSucceeded, orderId, payment.Text(x => x.At("id")), amount, currency?.ToUpperInvariant()));
                }

            case "payment_link.expired" or "payment_link.cancelled":
                return WebhookParseResult.Of(new NormalisedPaymentEvent(
                    ProviderName, eventId, PaymentEventType.CheckoutExpired, link.Id(x => x.At("reference_id")) ?? link.Id(x => x.At("notes", "orderId")), null, null, null));

            case "payment.failed":
                {
                    // A failed attempt does not end a payment link (the customer may retry), but it is worth knowing; only when we can tie it to an order.
                    var orderId = payment.Id(x => x.At("notes", "orderId"));
                    return orderId is null
                        ? WebhookParseResult.NotRelevant
                        : WebhookParseResult.Of(new NormalisedPaymentEvent(ProviderName, eventId, PaymentEventType.PaymentFailed, orderId, null, null, null));
                }

            case "refund.created":
                {
                    var refund = root.At("payload", "refund", "entity");
                    return WebhookParseResult.Of(new NormalisedPaymentEvent(
                        ProviderName, eventId, PaymentEventType.RefundCreated, refund.Id(x => x.At("notes", "orderId")), refund.Text(x => x.At("payment_id")),
                        refund.Number(x => x.At("amount")), refund.Text(x => x.At("currency"))?.ToUpperInvariant(), refund.Text(x => x.At("id"))));
                }

            default:
                return WebhookParseResult.NotRelevant;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private HttpRequestMessage Request(HttpMethod method, string path, object? json)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((_options.KeyId ?? string.Empty) + ":" + (_options.KeySecret ?? string.Empty))));
        if (json is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(json, JsonOptions), Encoding.UTF8, "application/json");
        }

        return request;
    }
}

internal static class RazorpayJson
{
    public static Guid? Id(this JsonElement? element, Func<JsonElement, JsonElement?> select) => element is { } e ? select(e).Id() : null;

    public static string? Text(this JsonElement? element, Func<JsonElement, JsonElement?> select) => element is { } e ? select(e).Text() : null;

    public static long? Number(this JsonElement? element, Func<JsonElement, JsonElement?> select) => element is { } e ? select(e).Number() : null;
}
