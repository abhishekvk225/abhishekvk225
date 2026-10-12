using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NexaVerify.Application.Verification.Abstractions;

namespace NexaVerify.Application.Verification.Parsing;

/// <summary>Small helpers shared by the parsers. All regexes are culture-invariant and have a match timeout (ReDoS).</summary>
public static class TextSupport
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Compiles (and caches) a pattern with the invariant culture and the shared match timeout. Throws <see cref="ArgumentException"/> for an invalid pattern.</summary>
    public static Regex Compile(string pattern, bool ignoreCase = false)
    {
        var key = (ignoreCase ? "i:" : "s:") + pattern;
        return Cache.GetOrAdd(key, _ => new Regex(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
            RegexTimeout));
    }

    /// <summary>Match that treats a timeout as "no match" (a hostile line must not stall or fail the whole document).</summary>
    public static Match? SafeMatch(Regex regex, string input)
    {
        try
        {
            var m = regex.Match(input);
            return m.Success ? m : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>All non-empty trimmed lines of all pages, in reading order.</summary>
    public static List<string> Lines(DocumentText text) =>
        text.Pages.SelectMany(p => p.Lines).Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();

    public static string Collapse(string value) => Regex.Replace(value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant, RegexTimeout);

    public static bool HasLetter(string value) => value.Any(char.IsLetter);
}
