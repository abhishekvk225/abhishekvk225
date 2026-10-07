using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Api.Configuration;

public sealed class SecurityHeadersOptions
{
    public const string SectionName = "SecurityHeaders";

    public bool Enabled { get; set; } = true;

    /// <summary>Content-Security-Policy value for API responses (the API serves JSON, so it is strict).</summary>
    [Required]
    public string ContentSecurityPolicy { get; set; } = "default-src 'none'; frame-ancestors 'none'";

    [Required]
    public string ReferrerPolicy { get; set; } = "no-referrer";

    [Required]
    public string PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=()";

    /// <summary>Seconds for Strict-Transport-Security max-age; 0 disables the header (development).</summary>
    [Range(0, 63072000)]
    public int HstsMaxAgeSeconds { get; set; } = 31536000;
}

public sealed class CorsAllowListOptions
{
    public const string SectionName = "Cors";
    public const string PolicyName = "NexaVerifyAllowList";

    /// <summary>Explicit https origins allowed to call the API from a browser. Empty = no cross-origin access. Never "*".</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
