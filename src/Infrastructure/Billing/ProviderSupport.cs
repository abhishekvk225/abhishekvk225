using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexaVerify.Application.Billing;

namespace NexaVerify.Infrastructure.Billing;

/// <summary>HMAC-SHA256 signing and constant-time comparison shared by the provider adapters.</summary>
public static class WebhookSignatures
{
    public static byte[] Hmac(string secret, ReadOnlySpan<byte> data) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);

    /// <summary>
    /// True when <paramref name="presentedHex"/> is the hex encoding of <paramref name="expected"/>. The bytes are compared with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>; only the (public) length and the hex syntax can short-circuit.
    /// </summary>
    public static bool HexMatches(string? presentedHex, byte[] expected)
    {
        if (string.IsNullOrWhiteSpace(presentedHex))
        {
            return false;
        }

        var trimmed = presentedHex.Trim();
        if (trimmed.Length != expected.Length * 2)
        {
            return false;
        }

        return trimmed.All(char.IsAsciiHexDigit) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(trimmed), expected);
    }

    public static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>Defensive reading of provider JSON: a missing or differently typed member is "absent", never an exception.</summary>
internal static class ProviderJson
{
    public static JsonDocumentOptions Options { get; } = new() { MaxDepth = 16 };

    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind != JsonValueKind.Null ? child : null;

    public static JsonElement? At(this JsonElement element, params string[] path)
    {
        JsonElement? current = element;
        foreach (var name in path)
        {
            current = current?.Child(name);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static string? Text(this JsonElement? element) => element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public static long? Number(this JsonElement? element) => element is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var n) ? n : null;

    public static Guid? Id(this JsonElement? element) => Guid.TryParse(element.Text(), out var id) && id != Guid.Empty ? id : null;

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Shared HTTP plumbing: every failure becomes a <see cref="PaymentProviderException"/> whose message never carries a key or a response body.</summary>
internal static class ProviderHttp
{
    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, string provider, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new PaymentProviderException($"{provider} answered HTTP {(int)response.StatusCode}{ErrorCode(body)}.");
            }

            return JsonDocument.Parse(body, ProviderJson.Options);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderException($"{provider} could not be reached ({ex.GetType().Name}).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaymentProviderException($"{provider} did not answer in time.", ex);
        }
        catch (JsonException ex)
        {
            throw new PaymentProviderException($"{provider} sent an answer that could not be read.", ex);
        }
    }

    /// <summary>Only the provider's short error code (for example <c>card_declined</c>), if any: never free text.</summary>
    private static string ErrorCode(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, ProviderJson.Options);
            var code = doc.RootElement.At("error", "code").Text();
            return code is { Length: > 0 and <= 60 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? $" ({code})" : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
