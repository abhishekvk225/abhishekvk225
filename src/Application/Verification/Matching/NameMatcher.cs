using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Matching;

/// <summary>
/// Deterministic, culture-invariant name comparison (docs/07 §6.7): Unicode normalisation, honorific removal, order-insensitive token
/// assignment with Damerau-Levenshtein similarity, initials and a small set of transliteration-safe spelling rules. The score is
/// server-side only.
/// </summary>
public sealed class NameMatcher : INameMatcher
{
    private const int MaxInputLength = 200;
    private const int MaxTokens = 12;

    private readonly NameOptions _options;
    private readonly HashSet<string> _ignored;

    public NameMatcher(IOptions<VerificationOptions> options)
    {
        _options = options.Value.Names;
        _ignored = new HashSet<string>(_options.IgnoredTokens.Select(t => t.Trim().ToUpperInvariant()), StringComparer.Ordinal);
    }

    public NameMatch Compare(string? submitted, string? extracted)
    {
        if (string.IsNullOrWhiteSpace(submitted))
        {
            return new NameMatch(FieldMatchOutcome.NotProvided, 0m);
        }

        if (string.IsNullOrWhiteSpace(extracted))
        {
            return new NameMatch(FieldMatchOutcome.NotExtracted, 0m);
        }

        var a = Tokens(submitted);
        var b = Tokens(extracted);
        if (a.Count == 0)
        {
            return new NameMatch(FieldMatchOutcome.NotProvided, 0m);
        }

        if (b.Count == 0)
        {
            return new NameMatch(FieldMatchOutcome.NotExtracted, 0m);
        }

        var shorter = a.Count <= b.Count ? a : b;
        var longer = a.Count <= b.Count ? b : a;

        // Greedy one-to-one assignment on descending similarity; ties broken by position, so the result is deterministic.
        var pairs = new List<(int S, int L, decimal Sim)>();
        for (var i = 0; i < shorter.Count; i++)
        {
            for (var j = 0; j < longer.Count; j++)
            {
                pairs.Add((i, j, Similarity(shorter[i], longer[j])));
            }
        }

        var usedS = new bool[shorter.Count];
        var usedL = new bool[longer.Count];
        var best = new decimal[shorter.Count];
        var assignedInitialOnly = new bool[shorter.Count];
        foreach (var p in pairs.OrderByDescending(p => p.Sim).ThenBy(p => p.S).ThenBy(p => p.L))
        {
            if (usedS[p.S] || usedL[p.L])
            {
                continue;
            }

            usedS[p.S] = true;
            usedL[p.L] = true;
            best[p.S] = p.Sim;
            assignedInitialOnly[p.S] = shorter[p.S].Length == 1 || longer[p.L].Length == 1;
        }

        var score = best.Average();
        var allMatched = best.All(s => s >= _options.MatchThreshold);
        var hasFullToken = Enumerable.Range(0, best.Length).Any(i => best[i] >= _options.MatchThreshold && !assignedInitialOnly[i]);
        var unmatchedLonger = longer.Count - shorter.Count;

        FieldMatchOutcome outcome;
        if (allMatched && hasFullToken)
        {
            outcome = unmatchedLonger <= 1 ? FieldMatchOutcome.Match : FieldMatchOutcome.PartialMatch;
        }
        else if (allMatched || score >= _options.PartialThreshold)
        {
            outcome = FieldMatchOutcome.PartialMatch;
        }
        else
        {
            outcome = FieldMatchOutcome.Mismatch;
        }

        return new NameMatch(outcome, Math.Round(score, 4));
    }

    /// <summary>Upper-case letters only, split into tokens, honorifics removed. Public so other rules (PAN initial check) share one normaliser.</summary>
    public List<string> Tokens(string value)
    {
        var text = value.Length > MaxInputLength ? value[..MaxInputLength] : value;
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            sb.Append(char.IsLetter(ch) ? char.ToUpperInvariant(ch) : ' ');
        }

        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !_ignored.Contains(t))
            .Take(MaxTokens)
            .ToList();
    }

    private static decimal Similarity(string x, string y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return 1m;
        }

        // Initial: a single letter matches a token that starts with it.
        if (x.Length == 1 || y.Length == 1)
        {
            return x[0] == y[0] ? 1m : 0m;
        }

        var raw = Normalised(x, y);
        var cx = Canonical(x);
        var cy = Canonical(y);
        var canonical = string.Equals(cx, cy, StringComparison.Ordinal) ? 1m : Normalised(cx, cy);
        return Math.Max(raw, canonical);
    }

    private static decimal Normalised(string x, string y)
    {
        var max = Math.Max(x.Length, y.Length);
        return max == 0 ? 1m : 1m - ((decimal)Distance(x, y) / max);
    }

    /// <summary>Spelling variants that are common in romanised Indic and Arabic names; applied to BOTH sides, so it can only make equal names equal.</summary>
    private static string Canonical(string token)
    {
        var s = token;
        foreach (var (from, to) in Rules)
        {
            s = s.Replace(from, to, StringComparison.Ordinal);
        }

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (sb.Length == 0 || sb[^1] != ch)
            {
                sb.Append(ch); // collapse doubled letters
            }
        }

        return sb.ToString();
    }

    private static readonly (string From, string To)[] Rules =
    [
        ("PH", "F"), ("KH", "K"), ("GH", "G"), ("BH", "B"), ("DH", "D"), ("TH", "T"), ("SH", "S"), ("CH", "C"),
        ("W", "V"), ("OO", "U"), ("EE", "I"), ("Y", "I"),
    ];

    /// <summary>Optimal string alignment distance (Damerau-Levenshtein with adjacent transpositions).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
