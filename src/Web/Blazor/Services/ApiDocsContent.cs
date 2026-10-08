using System.Reflection;
using System.Text.RegularExpressions;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Web.Services;

/// <summary>One documented code sample. <see cref="Code"/> is shown as plain text, never as markup.</summary>
public sealed record DocSample(string Language, string Code);

public sealed record DocEndpoint(string Id, string Title, string Method, string Path, string Summary, string Permission, IReadOnlyList<DocSample> Samples);

public sealed record DocErrorCode(string Code, string Meaning);

public sealed record DocEvent(string Type, string Description);

/// <summary>
/// Static content of the in-portal API guide. Samples use placeholders only (<c>YOUR_API_KEY</c>, a fictitious host): there are no
/// real keys, hosts or secrets anywhere in this file.
/// </summary>
public static class ApiDocsContent
{
    public const string BaseUrlPlaceholder = "https://YOUR-NEXAVERIFY-API-HOST/api/v1";

    private const string Key = "YOUR_API_KEY";

    public static IReadOnlyList<DocEndpoint> Endpoints { get; } =
    [
        new("enroll", "Register a person", "POST", "/faces/enroll",
            "Adds a person (or another photo of a known person). Send the photo and your own reference for the person. Needs the 'Register faces' permission on the key.",
            "faces.enroll",
            [
                new("curl", $$"""
                    curl -X POST "{{BaseUrlPlaceholder}}/faces/enroll" \
                      -H "X-Api-Key: {{Key}}" \
                      -H "Idempotency-Key: $(uuidgen)" \
                      -F "externalRef=EMP-1001" \
                      -F "displayName=Ada Lovelace" \
                      -F "consentReference=FORM-2026-0042" \
                      -F "image=@ada.jpg;type=image/jpeg"
                    """),
                new("C#", $$"""
                    using var http = new HttpClient { BaseAddress = new Uri("{{BaseUrlPlaceholder}}/") };
                    http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable("NEXAVERIFY_API_KEY"));

                    using var form = new MultipartFormDataContent
                    {
                        { new StringContent("EMP-1001"), "externalRef" },
                        { new StringContent("FORM-2026-0042"), "consentReference" },
                        { new ByteArrayContent(File.ReadAllBytes("ada.jpg")), "image", "ada.jpg" },
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post, "faces/enroll") { Content = form };
                    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

                    using var response = await http.SendAsync(request);
                    Console.WriteLine(await response.Content.ReadAsStringAsync());
                    """),
                new("JavaScript", $$"""
                    const form = new FormData();
                    form.append("externalRef", "EMP-1001");
                    form.append("consentReference", "FORM-2026-0042");
                    form.append("image", photoFile); // a File or Blob (JPEG, PNG or WebP, up to 5 MB)

                    const response = await fetch("{{BaseUrlPlaceholder}}/faces/enroll", {
                      method: "POST",
                      headers: { "X-Api-Key": process.env.NEXAVERIFY_API_KEY, "Idempotency-Key": crypto.randomUUID() },
                      body: form,
                    });
                    console.log(await response.json());
                    """),
            ]),
        new("verify", "Check one person (1:1)", "POST", "/faces/verify",
            "Compares a photo with one registered person, given by profileId or externalRef (exactly one of them). Answers match or no match, with the similarity score.",
            "faces.verify",
            [
                new("curl", $$"""
                    curl -X POST "{{BaseUrlPlaceholder}}/faces/verify" \
                      -H "X-Api-Key: {{Key}}" \
                      -H "Idempotency-Key: $(uuidgen)" \
                      -F "externalRef=EMP-1001" \
                      -F "image=@visitor.jpg;type=image/jpeg"
                    """),
                new("C#", $$"""
                    using var form = new MultipartFormDataContent
                    {
                        { new StringContent("EMP-1001"), "externalRef" },
                        { new ByteArrayContent(File.ReadAllBytes("visitor.jpg")), "image", "visitor.jpg" },
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post, "faces/verify") { Content = form };
                    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                    using var response = await http.SendAsync(request); // http: see the enroll example
                    """),
                new("JavaScript", $$"""
                    const form = new FormData();
                    form.append("externalRef", "EMP-1001");
                    form.append("image", visitorPhoto);
                    const response = await fetch("{{BaseUrlPlaceholder}}/faces/verify", {
                      method: "POST",
                      headers: { "X-Api-Key": process.env.NEXAVERIFY_API_KEY, "Idempotency-Key": crypto.randomUUID() },
                      body: form,
                    });
                    const result = await response.json(); // { match, score, threshold, outcome, credits, ... }
                    """),
            ]),
        new("identify", "Find a person (1:N)", "POST", "/faces/identify",
            "Searches all registered people for the closest matches to a photo. Optional topK (1 to 20) limits the number of candidates.",
            "faces.identify",
            [
                new("curl", $$"""
                    curl -X POST "{{BaseUrlPlaceholder}}/faces/identify" \
                      -H "X-Api-Key: {{Key}}" \
                      -H "Idempotency-Key: $(uuidgen)" \
                      -F "topK=5" \
                      -F "image=@visitor.jpg;type=image/jpeg"
                    """),
                new("C#", $$"""
                    using var form = new MultipartFormDataContent
                    {
                        { new StringContent("5"), "topK" },
                        { new ByteArrayContent(File.ReadAllBytes("visitor.jpg")), "image", "visitor.jpg" },
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post, "faces/identify") { Content = form };
                    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                    using var response = await http.SendAsync(request);
                    """),
                new("JavaScript", $$"""
                    const form = new FormData();
                    form.append("topK", "5");
                    form.append("image", visitorPhoto);
                    const response = await fetch("{{BaseUrlPlaceholder}}/faces/identify", {
                      method: "POST",
                      headers: { "X-Api-Key": process.env.NEXAVERIFY_API_KEY, "Idempotency-Key": crypto.randomUUID() },
                      body: form,
                    });
                    const { matches, bestScore, outcome } = await response.json();
                    """),
            ]),
        new("balance", "Check your credits", "GET", "/faces/balance",
            "Returns how many credits you have left and when they expire.",
            "faces.read",
            [
                new("curl", $$"""
                    curl "{{BaseUrlPlaceholder}}/faces/balance" -H "X-Api-Key: {{Key}}"
                    """),
                new("C#", $$"""
                    var balance = await http.GetFromJsonAsync<JsonElement>("faces/balance"); // http: see the enroll example
                    Console.WriteLine(balance.GetProperty("remaining").GetInt32());
                    """),
                new("JavaScript", $$"""
                    const response = await fetch("{{BaseUrlPlaceholder}}/faces/balance", {
                      headers: { "X-Api-Key": process.env.NEXAVERIFY_API_KEY },
                    });
                    const { remaining, expiresAt, status } = await response.json();
                    """),
            ]),
    ];

    public static DocSample[] WebhookVerification { get; } =
    [
        new("C#", """
            using System.Globalization;
            using System.Security.Cryptography;
            using System.Text;

            // header: the X-Signature value, for example "t=1760000000,v1=9f2c...". body: the raw request body, exactly as received.
            static bool IsValid(string secret, string header, string body, TimeSpan tolerance)
            {
                string? t = null, v1 = null;
                foreach (var part in header.Split(','))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) continue;
                    if (pair[0].Trim() == "t") t = pair[1].Trim();
                    else if (pair[0].Trim() == "v1") v1 = pair[1].Trim();
                }

                // The timestamp must be plain digits and a sensible date, otherwise the message is rejected.
                if (t is null || v1 is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 253402300799)
                {
                    return false;
                }

                // Reject old messages (replays) and messages dated in the future (allowing one minute of clock difference).
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (age > tolerance || age < TimeSpan.FromMinutes(-1))
                {
                    return false;
                }

                // Malformed hex is simply "not valid", never an exception.
                var received = new byte[32];
                if (!Convert.TryFromHexString(v1, received, out var written) || written != received.Length)
                {
                    return false;
                }

                var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
                return CryptographicOperations.FixedTimeEquals(received, expected);
            }
            """),
        new("JavaScript", """
            import crypto from "node:crypto";

            // header: the X-Signature value, body: the raw request body (a string, before any JSON parsing).
            export function isValid(secret, header, body, toleranceSeconds = 300) {
              const parts = {};
              for (const part of header.split(",")) {
                const i = part.indexOf("=");
                if (i > 0) parts[part.slice(0, i).trim()] = part.slice(i + 1).trim();
              }

              // The timestamp must be plain digits and the signature 64 hex characters, otherwise the message is rejected.
              if (!/^\d{1,12}$/.test(parts.t ?? "") || !/^[0-9a-fA-F]{64}$/.test(parts.v1 ?? "")) return false;

              // Reject old messages (replays) and messages dated in the future (allowing one minute of clock difference).
              const age = Date.now() / 1000 - Number(parts.t);
              if (age > toleranceSeconds || age < -60) return false;

              const expected = crypto.createHmac("sha256", secret).update(`${parts.t}.${body}`).digest();
              return crypto.timingSafeEqual(Buffer.from(parts.v1, "hex"), expected);
            }
            """),
    ];

    public static string WebhookPayloadExample { get; } = """
        {
          "id": "6f1d3c52-0f0e-4d1e-9d32-1b6f3f6c2a10",
          "type": "license.low_balance",
          "createdAt": "2026-06-15T09:00:00Z",
          "data": { "...": "details depend on the event" }
        }
        """;

    public static IReadOnlyList<DocEvent> Events { get; } =
    [
        new("recognition.completed", "A verification, identification or registration finished."),
        new("license.low_balance", "Remaining credits fell below your alert threshold."),
        new("license.expiring", "A license is about to expire."),
        new("license.expired", "A license expired."),
        new("license.exhausted", "All credits were used."),
        new("apikey.expiring", "An API key is about to expire."),
        new("webhook.test", "Sent when you press 'Send test event'."),
    ];

    public static DocSample ErrorExample { get; } = new("JSON", """
        {
          "status": 402,
          "code": "LICENSE_INSUFFICIENT_BALANCE",
          "detail": "Not enough credits for this operation.",
          "correlationId": "0HN7K4Q2D1V3P"
        }
        """);

    public static DocSample RetrySample { get; } = new("JavaScript", $$"""
        async function callWithRetry(makeRequest, attempts = 4) {
          for (let i = 0; i < attempts; i++) {
            const response = await makeRequest();
            if (response.status !== 429 && response.status < 500) return response;
            const wait = Number(response.headers.get("Retry-After") ?? 2 ** i);
            await new Promise((r) => setTimeout(r, wait * 1000));
          }
          throw new Error("Giving up after repeated rate limits or server errors");
        }
        """);

    /// <summary>All stable error codes of the public API, read from the shared contract so the guide cannot drift from the API.</summary>
    public static IReadOnlyList<DocErrorCode> ErrorCodeList { get; } = typeof(ErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .Select(code => new DocErrorCode(code, Meaning(code)))
        .ToList();

    private static string Meaning(string code) => code switch
    {
        ErrorCodes.ValidationFailed => "Some of the details you sent are not valid. The response lists which ones.",
        ErrorCodes.ImageInvalid => "The image is missing, empty or not a readable photo.",
        ErrorCodes.ImageTooLarge => "The photo is larger than 5 MB.",
        ErrorCodes.ImageUnsupportedType => "Only JPEG, PNG and WebP photos are accepted.",
        ErrorCodes.Unauthenticated => "No valid credentials were sent.",
        ErrorCodes.TokenExpired => "The sign-in token has expired.",
        ErrorCodes.ApiKeyInvalid => "The API key is not recognised.",
        ErrorCodes.ApiKeyExpired => "The API key has expired.",
        ErrorCodes.ApiKeyRevoked => "The API key was revoked.",
        ErrorCodes.LicenseNotFound => "There is no license on the account.",
        ErrorCodes.LicenseExpired => "The license has expired.",
        ErrorCodes.LicenseInsufficientBalance => "Not enough credits left for this operation.",
        ErrorCodes.LicenseSuspended => "The license is suspended.",
        ErrorCodes.LicenseInvalidTransition => "That change is not allowed for the license in its current state.",
        ErrorCodes.Forbidden => "The key or user is not allowed to do this.",
        ErrorCodes.ClientSuspended => "The account is suspended.",
        ErrorCodes.ClientInactive => "The account is not active.",
        ErrorCodes.IpNotAllowed => "The call came from an address that is not on the key's allow-list.",
        ErrorCodes.NotFound => "The thing you asked for does not exist.",
        ErrorCodes.Conflict => "That conflicts with the current state.",
        ErrorCodes.DuplicateExternalRef => "A person with this reference already exists.",
        ErrorCodes.ConcurrencyConflict => "Someone changed it at the same time. Load it again and retry.",
        ErrorCodes.PayloadTooLarge => "The request is too large.",
        ErrorCodes.NoFaceDetected => "No face was found in the photo.",
        ErrorCodes.MultipleFaces => "More than one face was found in the photo.",
        ErrorCodes.LowQualityImage => "The photo is too blurry, dark or small to use.",
        ErrorCodes.RateLimited => "Too many requests. Wait for the number of seconds in the Retry-After header.",
        ErrorCodes.DailyQuotaExceeded => "The daily request allowance was used up.",
        ErrorCodes.MethodNotAllowed => "That HTTP method is not supported here.",
        ErrorCodes.UnsupportedMediaType => "Send the request in the format the endpoint expects (for example multipart form data).",
        ErrorCodes.RequestTimeout => "The request took too long.",
        ErrorCodes.InternalError => "Something went wrong on our side. Retry, and quote the correlation id if it persists.",
        ErrorCodes.FaceProviderUnavailable => "The face engine is temporarily unavailable. You are not charged; try again in a moment.",
        _ => Humanize(code),
    };

    private static string Humanize(string code) => Regex.Replace(code.ToLowerInvariant().Replace('_', ' '), "^.", m => m.Value.ToUpperInvariant(), RegexOptions.None, TimeSpan.FromMilliseconds(100)) + ".";
}
