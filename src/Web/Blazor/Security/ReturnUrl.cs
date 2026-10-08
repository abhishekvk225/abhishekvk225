namespace NexaVerify.Web.Security;

/// <summary>Open-redirect protection: only same-site, relative paths are ever followed after sign-in.</summary>
public static class ReturnUrl
{
    public const int MaxLength = 512;

    /// <summary>True for a single-slash relative path such as <c>/admin/clients?page=2</c>.</summary>
    public static bool IsSafe(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxLength || url[0] != '/')
        {
            return false;
        }

        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return false; // protocol-relative (//evil) or /\evil
        }

        foreach (var c in url)
        {
            if (c == '\\' || char.IsControl(c))
            {
                return false;
            }
        }

        // Percent-encoded slashes/backslashes can be decoded into a protocol-relative URL by some clients.
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(url);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (decoded.StartsWith("//", StringComparison.Ordinal) || decoded.Contains('\\') || decoded.Contains("://", StringComparison.Ordinal)
            || decoded.Any(char.IsControl))
        {
            return false;
        }

        return !url.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("/login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A return URL is only honoured when it stays inside the portal the user belongs to.</summary>
    public static string ResolveForPortal(string? url, string portal)
    {
        var home = HomeFor(portal);
        if (!IsSafe(url))
        {
            return home;
        }

        var path = url!.Split('?', '#')[0];
        var inPortal = path.Equals(home, StringComparison.OrdinalIgnoreCase) || path.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase);
        return inPortal ? url! : home;
    }

    public static string HomeFor(string portal) => portal == PortalKinds.Admin ? "/admin" : "/client";
}
