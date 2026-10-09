using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Public;

namespace NexaVerify.Infrastructure.Security;

/// <summary>
/// Verifies Cloudflare Turnstile tokens server-side. With <c>Captcha:Provider = none</c> it accepts everything; once a provider is
/// configured it fails closed: a missing token, a rejected token, a timeout, a transport error or an unreadable answer all mean "not
/// verified". The token and the secret are never logged.
/// </summary>
public sealed class TurnstileCaptchaVerifier : ICaptchaVerifier
{
    private const int MaxTokenLength = 4096;

    private readonly HttpClient _http;
    private readonly CaptchaOptions _options;
    private readonly ILogger<TurnstileCaptchaVerifier> _logger;

    public TurnstileCaptchaVerifier(HttpClient http, IOptions<CaptchaOptions> options, ILogger<TurnstileCaptchaVerifier> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("secret", _options.SecretKey!),
                new("response", token),
            };
            if (!string.IsNullOrWhiteSpace(remoteIp))
            {
                form.Add(new("remoteip", remoteIp));
            }

            using var response = await _http.PostAsync(_options.VerifyUrl, new FormUrlEncodedContent(form), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Captcha provider answered {StatusCode}; treating the check as failed", (int)response.StatusCode);
                return false;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Captcha verification could not be completed ({Reason}); treating the check as failed", ex.GetType().Name);
            return false;
        }
    }
}
