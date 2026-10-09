namespace NexaVerify.Web.Services;

/// <summary>
/// The only gate between "the API gave us a payment address" and "we send the browser there". The address must be an absolute
/// <c>https</c> URL with a host and no embedded credentials. In Development/Testing the simulator page of this very portal
/// (<c>/dev/pay/{orderId}</c>, over http or https, on the portal's own host) is accepted as well. Anything else - <c>javascript:</c>,
/// <c>data:</c>, <c>http:</c>, relative or protocol-relative addresses, a look-alike host - is refused.
/// </summary>
public static class CheckoutRedirect
{
    public const string DevPayPrefix = "/dev/pay/";

    public const int MaxLength = 2048;

    /// <summary>True only in Development/Testing, where the portal's payment simulator may be used.</summary>
    public static bool IsDevHost(Microsoft.Extensions.Hosting.IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    /// <summary>The address to navigate to, or null when it is not safe.</summary>
    /// <param name="raw">What the API returned.</param>
    /// <param name="allowDevPay">True only in Development/Testing.</param>
    /// <param name="portalBase">The portal's own base address (to recognise its simulator page).</param>
    public static Uri? Validate(string? raw, bool allowDevPay, Uri? portalBase = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength || raw.Any(char.IsControl) || raw.Contains('\\') || !raw.Equals(raw.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        if (allowDevPay && raw.StartsWith(DevPayPrefix, StringComparison.Ordinal) && !raw.StartsWith("//", StringComparison.Ordinal) && portalBase is not null
            && Uri.TryCreate(portalBase, raw, out var relative) && IsDevPay(relative, portalBase))
        {
            return relative;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri;
        }

        return allowDevPay && portalBase is not null && IsDevPay(uri, portalBase) ? uri : null;
    }

    private static bool IsDevPay(Uri uri, Uri portalBase) =>
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && SameHost(uri, portalBase)
        && uri.AbsolutePath.StartsWith(DevPayPrefix, StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool SameHost(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
