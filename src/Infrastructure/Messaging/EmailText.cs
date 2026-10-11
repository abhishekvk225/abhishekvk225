using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NexaVerify.Infrastructure.Messaging;

/// <summary>
/// Text hygiene for everything that ends up in a mail. Header values (subject, display names) can never carry a line break, so a
/// user-supplied name cannot add headers or recipients; body values lose control characters; error texts lose secrets.
/// </summary>
public static partial class EmailText
{
    public const int MaxSubjectLength = 200;

    /// <summary>One line: every control character, line break and Unicode line/paragraph separator becomes a space; runs of white space collapse.</summary>
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '\u2028' or '\u2029' or '\u200B' or '\u202E' or '\u202D' or '\u2066' or '\u2067' or '\u2068' or '\u2069')
            {
                if (!space && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                space = true;
                continue;
            }

            sb.Append(c);
            space = false;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Several lines allowed (a contact message): CRLF/CR become LF, other control characters are dropped.</summary>
    public static string MultiLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalised = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n');
        var sb = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>A safe Subject header: one line, no control characters, at most <see cref="MaxSubjectLength"/> characters.</summary>
    public static string Subject(string? value)
    {
        var line = SingleLine(value);
        return line.Length <= MaxSubjectLength ? line : line[..(MaxSubjectLength - 1)].TrimEnd() + "\u2026";
    }

    /// <summary>A safe display name for a header: one line, without quotes or angle brackets, at most 100 characters; null when nothing is left.</summary>
    public static string? DisplayName(string? value)
    {
        var line = SingleLine(value).Replace("\"", string.Empty, StringComparison.Ordinal).Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal).Trim();
        if (line.Length > 100)
        {
            line = line[..100];
        }

        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// A failure text safe to store and show to administrators: one line, bounded, with every given secret value removed. It never carries
    /// message content; server replies are kept because that is what an operator needs to fix the problem.
    /// </summary>
    public static string Error(string? text, int maxLength, IEnumerable<string?>? secrets = null)
    {
        var line = MailboxPattern().Replace(SingleLine(text), "***@***"); // server replies often echo the recipient; stored errors are shown to people who may not see addresses
        foreach (var secret in secrets ?? [])
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 3)
            {
                line = line.Replace(secret, "***", StringComparison.OrdinalIgnoreCase);
            }
        }

        return line.Length <= maxLength ? line : line[..maxLength];
    }

    public static string Format(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, open - i);
            var key = template[(open + 1)..close];
            if (values.TryGetValue(key, out var value))
            {
                sb.Append(value);
            }
            else
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The email template refers to the variable '{key}' that the caller did not supply."));
            }

            i = close + 1;
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"[^\s<>@,;:""']+@[^\s<>@,;:""']+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex MailboxPattern();
}
