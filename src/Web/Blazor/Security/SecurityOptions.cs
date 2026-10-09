namespace NexaVerify.Web.Security;

/// <summary>Config-driven security headers (<c>Security:Headers</c>, docs/04 §5).</summary>
public sealed class SecurityHeadersOptions
{
    public const string Section = "Security:Headers";

    public bool Enabled { get; set; } = true;

    public bool ContentSecurityPolicyEnabled { get; set; } = true;

    /// <summary>Allow <c>ws:</c> in connect-src (plain-HTTP local development only; production keeps <c>wss:</c>).</summary>
    public bool AllowInsecureWebSockets { get; set; }

    /// <summary>Origin of the API when the browser calls it directly (normally empty: the BFF calls it server-side).</summary>
    public string ApiOrigin { get; set; } = string.Empty;

    /// <summary>Additional space-separated script-src sources (e.g. a hot-reload host in dev). Never put 'unsafe-inline' here.</summary>
    public string ExtraScriptSrc { get; set; } = string.Empty;

    public bool CameraAllowedForSelf { get; set; } = true;

    /// <summary>
    /// Set to <c>true</c> when the API's sign-up bot check is Cloudflare Turnstile (the API's <c>/public/config</c> says <c>turnstile</c>).
    /// The CSP then also allows Turnstile's origin, on the sign-up page only. Off by default: the page then allows nothing extra.
    /// </summary>
    public bool TurnstileEnabled { get; set; }
}

/// <summary>Cookie hardening (<c>Security:Cookies</c>).</summary>
public sealed class CookieSecurityOptions
{
    public const string Section = "Security:Cookies";

    /// <summary>Cookies are Secure-only. Switch off only for plain-HTTP local development.</summary>
    public bool RequireSecure { get; set; } = true;

    public string SameSite { get; set; } = "Strict";

    public Microsoft.AspNetCore.Http.SameSiteMode SameSiteMode =>
        Enum.TryParse<Microsoft.AspNetCore.Http.SameSiteMode>(SameSite, true, out var m) && m != Microsoft.AspNetCore.Http.SameSiteMode.Unspecified
            ? m
            : Microsoft.AspNetCore.Http.SameSiteMode.Strict;
}
