using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace NexaVerify.Application.Identity;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 20)]
    public int MaxFailedAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Refresh token lifetime, renewed on every rotation.</summary>
    [Range(1, 90)]
    public int RefreshTokenSlidingDays { get; set; } = 7;

    /// <summary>Hard cap for a whole refresh-token family regardless of renewals.</summary>
    [Range(1, 365)]
    public int RefreshTokenAbsoluteDays { get; set; } = 30;

    [Range(5, 1440)]
    public int PasswordResetMinutes { get; set; } = 30;

    /// <summary>Link sent in reset emails. Must contain {email} and {token}; never built from the request's Host header.</summary>
    [Required]
    public string PasswordResetUrlTemplate { get; set; } = "https://localhost:7200/reset-password?email={email}&token={token}";
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var template = options.PasswordResetUrlTemplate;
        if (!template.Contains("{token}", StringComparison.Ordinal) || !template.Contains("{email}", StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail("Auth:PasswordResetUrlTemplate must contain {email} and {token}.");
        }

        var probe = template.Replace("{email}", "x", StringComparison.Ordinal).Replace("{token}", "x", StringComparison.Ordinal);
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            return ValidateOptionsResult.Fail("Auth:PasswordResetUrlTemplate must be an absolute https URL (http only for localhost).");
        }

        return options.RefreshTokenAbsoluteDays < options.RefreshTokenSlidingDays
            ? ValidateOptionsResult.Fail("Auth:RefreshTokenAbsoluteDays must be >= RefreshTokenSlidingDays.")
            : ValidateOptionsResult.Success;
    }
}

public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    [Range(8, 128)]
    public int MinLength { get; set; } = 12;

    [Range(16, 1024)]
    public int MaxLength { get; set; } = 128;
}
