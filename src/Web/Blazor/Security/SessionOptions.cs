using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Web.Security;

/// <summary>Server-side session settings (<c>Session:*</c>, deploy/CONFIG.md).</summary>
public sealed class SessionOptions
{
    public const string Section = "Session";

    /// <summary>Name of the opaque session cookie.</summary>
    public string CookieName { get; set; } = "nv.session";

    /// <summary>A session with no activity for this long is gone (sliding window).</summary>
    [Range(1, 24 * 60)]
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>A session never lives longer than this, however active (absolute window).</summary>
    [Range(1, 24 * 14)]
    public int AbsoluteTimeoutHours { get; set; } = 12;

    /// <summary>Refresh the access token this many seconds before it expires, so calls rarely hit a 401 first.</summary>
    [Range(0, 300)]
    public int RefreshSkewSeconds { get; set; } = 30;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);

    public TimeSpan AbsoluteTimeout => TimeSpan.FromHours(AbsoluteTimeoutHours);
}

/// <summary>Where the portal finds the API (<c>Api:*</c>).</summary>
public sealed class ApiClientOptions
{
    public const string Section = "Api";

    public string BaseUrl { get; set; } = string.Empty;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Validates the base address: absolute, https everywhere except Development/Testing (where plain http is allowed).
    /// </summary>
    public static Uri ResolveBaseAddress(string? baseUrl, bool allowInsecure)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Api:BaseUrl must be set to the absolute address of the NexaVerify API.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Api:BaseUrl must use https outside Development.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Api:BaseUrl must not contain credentials.");
        }

        // Trailing slash so relative request paths resolve under the base path.
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/", UriKind.Absolute);
    }
}
