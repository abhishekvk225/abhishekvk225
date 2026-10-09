using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;

namespace NexaVerify.Web.Security;

/// <summary>Settings of the public website (<c>Site:*</c>).</summary>
public sealed class SiteOptions
{
    public const string Section = "Site";

    /// <summary>
    /// The public address of the website (for canonical links, sitemap and robots). Set it in production: without it the request's own
    /// scheme and host are used, which is only as trustworthy as the host filtering in front of the portal (<c>AllowedHosts</c>).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Name shown in the draft legal pages. Replace with the registered company name before launch.</summary>
    public string LegalEntityName { get; set; } = "NexaVerify";
}

/// <summary>The pages of the public marketing site: which paths are statically server-rendered and which appear in the sitemap.</summary>
public static class PublicRoutes
{
    public sealed record Entry(string Path, string Title, string Priority, bool InSitemap = true);

    public static IReadOnlyList<Entry> Pages { get; } =
    [
        new("/", "Home", "1.0"),
        new("/features", "Features", "0.8"),
        new("/how-it-works", "How it works", "0.7"),
        new("/pricing", "Pricing", "0.9"),
        new("/developers", "Developers", "0.7"),
        new("/security", "Security and privacy", "0.7"),
        new("/contact", "Contact", "0.5"),
        new("/signup", "Start free trial", "0.9"),
        new("/terms", "Terms of service (draft)", "0.2"),
        new("/privacy", "Privacy notice (draft)", "0.2"),
        new("/verify-email", "Confirm email", "0.1", InSitemap: false),
        new("/not-found", "Page not found", "0.1", InSitemap: false),
    ];

    /// <summary>A marketing page (or the 404 page): plain server-rendered HTML with its own styling, no MudBlazor providers, no circuit. "/" is matched exactly.</summary>
    public static bool IsMarketing(PathString path)
    {
        if (!path.HasValue || path.Value == "/")
        {
            return true;
        }

        return Pages.Any(p => p.Path != "/" && path.StartsWithSegments(p.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Paths that must be plain server-rendered HTML (no SignalR circuit): the marketing pages and the sign-in pages.</summary>
    public static bool IsStatic(PathString path) => IsMarketing(path) || path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase);

    /// <summary>Paths kept out of search engines: the signed-in areas and the account flows.</summary>
    public static IReadOnlyList<string> Disallowed { get; } =
    [
        "/admin", "/client", "/login", "/auth", "/bff", "/verify-email", "/forgot-password", "/reset-password", "/change-password", "/mfa", "/forbidden", "/styleguide",
    ];
}

/// <summary>Builds absolute public addresses without trusting arbitrary input.</summary>
public sealed class SiteUrls(IOptions<SiteOptions> options)
{
    private readonly SiteOptions _options = options.Value;

    /// <summary>The configured public base address, or null when it is missing or not an absolute http(s) address without credentials.</summary>
    public Uri? ConfiguredBase =>
        Uri.TryCreate(_options.PublicBaseUrl?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            ? uri
            : null;

    public string Absolute(string path, string? fallbackBase)
    {
        var baseUri = ConfiguredBase ?? (Uri.TryCreate(fallbackBase, UriKind.Absolute, out var f) ? f : new Uri("http://localhost/"));
        var trimmed = baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/');
        return path == "/" ? trimmed + "/" : trimmed + path;
    }
}

public static class SeoEndpoints
{
    public static void MapPublicSite(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (HttpContext http, SiteUrls urls) =>
        {
            var text = new StringBuilder("User-agent: *\n");
            foreach (var path in PublicRoutes.Disallowed)
            {
                text.Append("Disallow: ").Append(path).Append('\n');
            }

            text.Append("Allow: /\n\nSitemap: ").Append(urls.Absolute("/sitemap.xml", BaseOf(http))).Append('\n');
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(text.ToString(), "text/plain; charset=utf-8");
        }).AllowAnonymous();

        app.MapGet("/sitemap.xml", (HttpContext http, SiteUrls urls) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(BuildSitemap(urls, BaseOf(http)), "application/xml; charset=utf-8");
        }).AllowAnonymous();
    }

    public static string BuildSitemap(SiteUrls urls, string? fallbackBase)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach (var page in PublicRoutes.Pages.Where(p => p.InSitemap))
            {
                xml.WriteStartElement("url");
                xml.WriteElementString("loc", urls.Absolute(page.Path, fallbackBase));
                xml.WriteElementString("priority", page.Priority);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BaseOf(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/";
}
