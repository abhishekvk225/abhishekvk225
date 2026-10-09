using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace NexaVerify.Application.Public;

/// <summary>Self-service sign-up and the public contact form (<c>Signup:*</c>, deploy/CONFIG.md).</summary>
public sealed class SignupOptions
{
    public const string SectionName = "Signup";

    /// <summary>Master switch. When off, <c>POST /public/signup</c> answers 403 and the website hides the form.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Credits of the trial license created for a verified sign-up.</summary>
    [Range(1, 1_000_000)]
    public int TrialCredits { get; set; } = 100;

    [Range(1, 365)]
    public int TrialDays { get; set; } = 14;

    /// <summary>How long the emailed verification link stays valid.</summary>
    [Range(1, 168)]
    public int VerificationHours { get; set; } = 24;

    [Range(1, 100_000)]
    public int MaxSignupsPerIpPerHour { get; set; } = 5;

    [Range(1, 1000)]
    public int MaxSignupsPerEmailPerDay { get; set; } = 3;

    [Range(1, 100_000)]
    public int MaxContactsPerIpPerHour { get; set; } = 5;

    /// <summary>Minimum duration of sign-up responses, so timing does not reveal whether an address is known.</summary>
    [Range(0, 5000)]
    public int MinimumResponseMilliseconds { get; set; } = 400;

    /// <summary>Contact requests older than this are deleted.</summary>
    [Range(1, 3650)]
    public int ContactRetentionDays { get; set; } = 180;

    /// <summary>Where new contact requests are announced. Empty = stored only (read them through the admin API).</summary>
    [EmailAddress]
    public string? ContactNotifyEmail { get; set; }

    /// <summary>Extra domains refused at sign-up, on top of the built-in list of throw-away mail providers.</summary>
    public string[] DisposableEmailDomains { get; set; } = [];

    /// <summary>IANA zone given to new clients.</summary>
    [Required]
    [StringLength(64)]
    public string DefaultTimeZone { get; set; } = "UTC";

    [Range(1, 1440)]
    public int PurgeIntervalMinutes { get; set; } = 60;
}

/// <summary>Bot protection for the public forms (<c>Captcha:*</c>). Fails closed: with a provider configured, no valid token means no sign-up.</summary>
public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    public const string None = "none";
    public const string Turnstile = "turnstile";

    /// <summary><c>none</c> (default) or <c>turnstile</c> (Cloudflare Turnstile).</summary>
    [Required]
    public string Provider { get; set; } = None;

    /// <summary>Public site key handed to the website.</summary>
    public string? SiteKey { get; set; }

    /// <summary>Secret key used to verify tokens server-side. A secret: configure it through the secrets store.</summary>
    public string? SecretKey { get; set; }

    [Required]
    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    [Range(1, 30)]
    public int TimeoutSeconds { get; set; } = 5;

    public bool IsEnabled => string.Equals(Provider, Turnstile, StringComparison.OrdinalIgnoreCase);
}

public sealed class CaptchaOptionsValidator : IValidateOptions<CaptchaOptions>
{
    public ValidateOptionsResult Validate(string? name, CaptchaOptions options)
    {
        if (!string.Equals(options.Provider, CaptchaOptions.None, StringComparison.OrdinalIgnoreCase) && !options.IsEnabled)
        {
            return ValidateOptionsResult.Fail("Captcha:Provider must be 'none' or 'turnstile'.");
        }

        if (options.IsEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.SiteKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            {
                return ValidateOptionsResult.Fail("Captcha:SiteKey and Captcha:SecretKey are required when Captcha:Provider is 'turnstile'.");
            }

            if (!Uri.TryCreate(options.VerifyUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return ValidateOptionsResult.Fail("Captcha:VerifyUrl must be an absolute https URL.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Where the customer-facing portal lives; verification and "you already have an account" emails link there (never built from a request header).</summary>
public sealed class PortalLinksOptions
{
    public const string SectionName = "Portal";

    [Required]
    public string PublicBaseUrl { get; set; } = "https://localhost:7200";

    public string LinkTo(string path, params (string Key, string Value)[] query)
    {
        var url = PublicBaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        return query.Length == 0
            ? url
            : url + "?" + string.Join('&', query.Select(q => q.Key + "=" + Uri.EscapeDataString(q.Value)));
    }
}

public sealed class PortalLinksOptionsValidator : IValidateOptions<PortalLinksOptions>
{
    public ValidateOptionsResult Validate(string? name, PortalLinksOptions options) =>
        Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Portal:PublicBaseUrl must be an absolute https URL without query or fragment (http only for localhost).");
}
