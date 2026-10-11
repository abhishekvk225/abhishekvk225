using System.Text;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Messaging;
using NexaVerify.Application.Public;

namespace NexaVerify.Infrastructure.Messaging;

/// <summary>A link that would point outside the configured public base URL (or is otherwise unsafe). A programming error, never user input.</summary>
public sealed class EmailLinkException : InvalidOperationException
{
    public EmailLinkException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The only way a URL gets into a mail. A link is a relative path plus query pairs; the host, scheme and base path always come from
/// configuration (<c>Email:PublicBaseUrl</c>, else <c>Portal:PublicBaseUrl</c>), never from a request, a header or user input. Path segments
/// and query values are percent-encoded, and anything that could climb out of the base (<c>..</c>, a scheme, <c>//</c>, backslashes,
/// control characters) is refused.
/// </summary>
public sealed class EmailLinkBuilder
{
    private readonly Uri _base;

    public EmailLinkBuilder(IOptions<EmailOptions> email, IOptions<PortalLinksOptions> portal)
        : this(string.IsNullOrWhiteSpace(email.Value.PublicBaseUrl) ? portal.Value.PublicBaseUrl : email.Value.PublicBaseUrl)
    {
    }

    public EmailLinkBuilder(string publicBaseUrl)
    {
        if (!IsValidBase(publicBaseUrl))
        {
            throw new EmailLinkException("The public base URL for email links is not a valid absolute https URL.");
        }

        _base = new Uri(publicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public Uri BaseUri => _base;

    public static bool IsValidBase(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);

    public string Build(EmailLink link)
    {
        var path = link.Path;
        if (string.IsNullOrWhiteSpace(path) || path.Any(c => char.IsControl(c) || c is '\\' or '?' or '#' or '%' or ' ' or ':') || path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new EmailLinkException("An email link path must be a plain relative path.");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or ".."))
        {
            throw new EmailLinkException("An email link path must not climb out of the base URL.");
        }

        var sb = new StringBuilder(_base.GetLeftPart(UriPartial.Path));
        sb.Append(string.Join('/', segments.Select(Uri.EscapeDataString)));
        var first = true;
        foreach (var (key, value) in link.Query.Select(q => (q.Key, q.Value)))
        {
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new EmailLinkException("An email link query key may only contain letters, digits, '-' and '_'.");
            }

            sb.Append(first ? '?' : '&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        var built = new Uri(sb.ToString(), UriKind.Absolute);
        if (built.Scheme != _base.Scheme || built.Authority != _base.Authority || !built.AbsolutePath.StartsWith(_base.AbsolutePath, StringComparison.Ordinal))
        {
            throw new EmailLinkException("An email link must stay under the public base URL.");
        }

        return built.AbsoluteUri;
    }
}
