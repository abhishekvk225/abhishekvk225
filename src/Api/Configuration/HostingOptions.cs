using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Api.Configuration;

public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    /// <summary>Redirect plain-HTTP requests to HTTPS (health probes excepted). Honours forwarded headers when enabled.</summary>
    public bool RedirectToHttps { get; set; } = true;
}

/// <summary>
/// Trust for X-Forwarded-For / X-Forwarded-Proto. Disabled unless the deployment states exactly which proxies to trust;
/// trusting everything would let any client spoof its IP (rate limits, lockout, allow-lists) and scheme.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public bool Enabled { get; set; }

    public string[] KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges such as <c>10.0.0.0/8</c>.</summary>
    public string[] KnownNetworks { get; set; } = [];

    [Range(1, 5)]
    public int ForwardLimit { get; set; } = 1;
}

public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";
    public const string AuthPolicy = "auth";

    /// <summary>Requests per minute per client IP across the whole API (abuse backstop).</summary>
    [Range(1, 1000000)]
    public int PerIpPerMinute { get; set; } = 300;

    /// <summary>Requests per minute per client IP for credential endpoints (login, refresh, forgot/reset password).</summary>
    [Range(1, 1000000)]
    public int AuthPerIpPerMinute { get; set; } = 10;
}
