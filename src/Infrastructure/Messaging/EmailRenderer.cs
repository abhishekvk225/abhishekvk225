using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Messaging;

namespace NexaVerify.Infrastructure.Messaging;

/// <summary>A message ready to store and send. <see cref="HasOneTimeToken"/> tells the outbox to erase the bodies once the mail has gone out.</summary>
public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody, bool HasOneTimeToken);

public interface IEmailRenderer
{
    RenderedEmail Render(EmailRequest request);
}

/// <summary>
/// The single place where mail content is produced. Every caller value is sanitised and then HTML-encoded (or kept raw in the text part)
/// exactly once here; templates contain no HTML from callers, links come only from <see cref="EmailLinkBuilder"/>, and the layout is
/// inline-styled, table-based, remote-image-free and keeps readable contrast when a mail client darkens it.
/// </summary>
public sealed class EmailRenderer : IEmailRenderer
{
    // NexaVerify indigo and its dark-mode companion.
    private const string Indigo = "#4F46E5";
    private const string IndigoDark = "#818CF8";

    private readonly EmailOptions _options;
    private readonly EmailLinkBuilder _links;

    public EmailRenderer(IOptions<EmailOptions> options, EmailLinkBuilder links)
    {
        _options = options.Value;
        _links = links;
    }

    public RenderedEmail Render(EmailRequest request)
    {
        var content = EmailTemplateCatalog.For(request.Template);

        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var html = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Values)
        {
            var clean = value.Multiline ? EmailText.MultiLine(value.Value) : EmailText.SingleLine(value.Value);
            text[key] = clean;
            html[key] = value.Multiline ? Encode(clean).Replace("\n", "<br>", StringComparison.Ordinal) : Encode(clean);
        }

        var urls = request.Links.ToDictionary(l => l.Key, l => _links.Build(l.Value), StringComparer.Ordinal);

        var subject = EmailText.Subject(EmailText.Format(content.Subject, text));
        var preheader = EmailText.SingleLine(EmailText.Format(content.Preheader, text));
        var footer = _options.Footer.Length == 0 ? string.Empty : EmailText.MultiLine(_options.Footer);

        return new RenderedEmail(
            subject,
            BuildText(content, text, urls, footer),
            BuildHtml(content, subject, preheader, text, html, urls, footer),
            content.OneTimeToken);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    // ---------------------------------------------------------------- plain text

    private static string BuildText(EmailContent content, Dictionary<string, string> values, Dictionary<string, string> urls, string footer)
    {
        var sb = new StringBuilder();
        foreach (var block in content.Blocks)
        {
            switch (block)
            {
                case Para p:
                    sb.Append(EmailText.Format(p.Text, values)).Append("\n\n");
                    break;
                case Fine f:
                    sb.Append(EmailText.Format(f.Text, values)).Append("\n\n");
                    break;
                case Button b:
                    if (urls.TryGetValue(b.LinkKey, out var url))
                    {
                        sb.Append(b.Label).Append(":\n").Append(url).Append("\n\n");
                    }
                    else if (!b.Optional)
                    {
                        throw new InvalidOperationException($"The email template needs the link '{b.LinkKey}'.");
                    }

                    break;
                case Facts facts:
                    foreach (var (label, value) in facts.Rows)
                    {
                        sb.Append(label).Append(": ").Append(EmailText.Format(value, values)).Append('\n');
                    }

                    sb.Append('\n');
                    break;
                case Quote q:
                    sb.Append(values[q.Variable]).Append("\n\n");
                    break;
            }
        }

        sb.Append("--\n").Append(EmailText.Format(content.Reason, values)).Append('\n');
        if (footer.Length > 0)
        {
            sb.Append(footer).Append('\n');
        }

        sb.Append("NexaVerify\n");
        return sb.ToString(); // LF here; MimeKit writes CRLF on the wire
    }

    // ---------------------------------------------------------------- HTML

    private static string BuildHtml(
        EmailContent content, string subject, string preheader, Dictionary<string, string> text, Dictionary<string, string> html,
        Dictionary<string, string> urls, string footer)
    {
        var body = new StringBuilder();
        foreach (var block in content.Blocks)
        {
            switch (block)
            {
                case Para p:
                    body.Append("<p class=\"txt\" style=\"margin:0 0 16px 0;font-size:16px;line-height:24px;color:#1F2937;\">")
                        .Append(EmailText.Format(p.Text, html)).Append("</p>");
                    break;
                case Fine f:
                    body.Append("<p class=\"muted\" style=\"margin:8px 0 0 0;font-size:13px;line-height:20px;color:#6B7280;\">")
                        .Append(EmailText.Format(f.Text, html)).Append("</p>");
                    break;
                case Button b:
                    if (urls.TryGetValue(b.LinkKey, out var url))
                    {
                        AppendButton(body, b, url);
                    }
                    else if (!b.Optional)
                    {
                        throw new InvalidOperationException($"The email template needs the link '{b.LinkKey}'.");
                    }

                    break;
                case Facts facts:
                    body.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" style=\"margin:0 0 20px 0;border-collapse:collapse;\">");
                    foreach (var (label, value) in facts.Rows)
                    {
                        body.Append("<tr><td class=\"muted fact\" style=\"padding:6px 12px 6px 0;font-size:14px;color:#6B7280;vertical-align:top;white-space:nowrap;\">")
                            .Append(Encode(label))
                            .Append("</td><td class=\"txt fact\" style=\"padding:6px 0;font-size:14px;color:#1F2937;vertical-align:top;\">")
                            .Append(EmailText.Format(value, html)).Append("</td></tr>");
                    }

                    body.Append("</table>");
                    break;
                case Quote q:
                    body.Append("<blockquote class=\"quote txt\" style=\"margin:0 0 20px 0;padding:12px 16px;border-left:4px solid ").Append(Indigo)
                        .Append(";background-color:#EEF2FF;color:#1F2937;font-size:15px;line-height:24px;\">").Append(html[q.Variable]).Append("</blockquote>");
                    break;
            }
        }

        var reason = EmailText.Format(content.Reason, html);
        var footerHtml = footer.Length == 0 ? string.Empty : "<br>" + Encode(footer).Replace("\n", "<br>", StringComparison.Ordinal);
        var heading = Encode(EmailText.Format(content.Heading, text));

        return $$"""
            <!DOCTYPE html>
            <html lang="en" xmlns="http://www.w3.org/1999/xhtml">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <meta name="supported-color-schemes" content="light dark">
            <title>{{Encode(subject)}}</title>
            <style>
            :root { color-scheme: light dark; supported-color-schemes: light dark; }
            @media only screen and (max-width: 620px) { .container { width: 100% !important; } .pad { padding-left: 20px !important; padding-right: 20px !important; } }
            @media (prefers-color-scheme: dark) {
              .bg { background-color: #0F1020 !important; }
              .card { background-color: #1B1C33 !important; }
              .txt { color: #E5E7EB !important; }
              .muted { color: #9CA3AF !important; }
              .quote { background-color: #25264A !important; }
              .btn { background-color: {{IndigoDark}} !important; }
              .btn a { color: #0F1020 !important; }
              .btn2 a { color: {{IndigoDark}} !important; border-color: {{IndigoDark}} !important; }
            }
            </style>
            </head>
            <body class="bg" style="margin:0;padding:0;background-color:#F4F5FB;">
            <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:#F4F5FB;">{{Encode(preheader)}}</div>
            <table role="presentation" class="bg" cellpadding="0" cellspacing="0" border="0" width="100%" bgcolor="#F4F5FB" style="background-color:#F4F5FB;">
            <tr><td align="center" style="padding:24px 12px;">
            <table role="presentation" class="container" cellpadding="0" cellspacing="0" border="0" width="600" style="width:600px;max-width:600px;">
            <tr><td bgcolor="{{Indigo}}" style="background-color:{{Indigo}};padding:20px 32px;border-radius:8px 8px 0 0;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:22px;line-height:28px;font-weight:700;letter-spacing:0.2px;color:#FFFFFF;">NexaVerify</td></tr>
            <tr><td class="card pad" bgcolor="#FFFFFF" style="background-color:#FFFFFF;padding:32px;font-family:Segoe UI,Helvetica,Arial,sans-serif;">
            <h1 class="txt" style="margin:0 0 20px 0;font-size:22px;line-height:30px;font-weight:700;color:#111827;">{{heading}}</h1>
            {{body}}
            </td></tr>
            <tr><td class="card pad muted" bgcolor="#FFFFFF" style="background-color:#FFFFFF;padding:0 32px 28px 32px;border-radius:0 0 8px 8px;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:12px;line-height:18px;color:#6B7280;border-top:1px solid #E5E7EB;">
            <p class="muted" style="margin:16px 0 0 0;font-size:12px;line-height:18px;color:#6B7280;">{{reason}}{{footerHtml}}</p>
            </td></tr>
            </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    private static void AppendButton(StringBuilder body, Button button, string url)
    {
        var href = Encode(url);
        var label = Encode(button.Label);
        if (button.Primary)
        {
            body.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0 20px 0;\"><tr><td class=\"btn\" bgcolor=\"")
                .Append(Indigo).Append("\" style=\"background-color:").Append(Indigo).Append(";border-radius:6px;\">")
                .Append("<a href=\"").Append(href).Append("\" style=\"display:inline-block;padding:12px 24px;font-size:16px;font-weight:600;line-height:20px;color:#FFFFFF;text-decoration:none;border-radius:6px;\">")
                .Append(label).Append("</a></td></tr></table>");
        }
        else
        {
            body.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0 20px 0;\"><tr><td class=\"btn2\" style=\"border-radius:6px;\">")
                .Append("<a href=\"").Append(href).Append("\" style=\"display:inline-block;padding:10px 22px;font-size:15px;font-weight:600;line-height:20px;color:").Append(Indigo)
                .Append(";text-decoration:none;border:2px solid ").Append(Indigo).Append(";border-radius:6px;\">").Append(label).Append("</a></td></tr></table>");
        }

        // The address in the clear as well: mail clients that do not make buttons clickable, and people who want to see where a link goes.
        body.Append("<p class=\"muted\" style=\"margin:0 0 20px 0;font-size:12px;line-height:18px;color:#6B7280;word-break:break-all;\">").Append(href).Append("</p>");
    }
}
