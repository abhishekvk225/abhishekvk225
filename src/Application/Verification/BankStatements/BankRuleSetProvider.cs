using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;

namespace NexaVerify.Application.Verification.BankStatements;

/// <summary>
/// Loads bank rule sets as DATA: the embedded <c>generic</c> set and synthetic test layouts, plus every <c>*.json</c> in
/// <c>Verification:BankStatement:RuleSetsPath</c> when that directory exists (a file there with the same id overrides the embedded one).
/// Every pattern is compiled with the shared match timeout and checked for the required named groups, so a bad file fails start-up.
/// Real bank layouts need sample statements; none ships here.
/// </summary>
public sealed class BankRuleSetProvider : IBankRuleSetProvider
{
    public const string GenericId = "generic";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<BankRuleSet> _ruleSets;
    private readonly BankRuleSet _generic;

    public BankRuleSetProvider(IOptions<VerificationOptions> options, ILogger<BankRuleSetProvider>? logger = null)
    {
        var errors = new List<string>();
        _ruleSets = Load(options.Value.BankStatement.RuleSetsPath, errors, logger ?? NullLogger<BankRuleSetProvider>.Instance);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid bank rule sets: " + string.Join("; ", errors));
        }

        _generic = _ruleSets.First(r => r.Id == GenericId);
    }

    public IReadOnlyList<BankRuleSet> RuleSets => _ruleSets;

    public BankRuleSet Detect(DocumentText text)
    {
        var head = string.Join('\n', text.Pages.Take(2).SelectMany(p => p.Lines).Select(l => l.Text));
        BankRuleSet? best = null;
        var bestScore = 0;
        foreach (var rs in _ruleSets)
        {
            var score = rs.DetectPatterns.Count(p => TextSupport.SafeMatch(TextSupport.Compile(p), head) is not null);
            if (score > bestScore)
            {
                best = rs;
                bestScore = score;
            }
        }

        return best ?? _generic;
    }

    /// <summary>Loads and validates; used by the provider and by the start-up options validator. Never throws for bad data: it reports.</summary>
    public static IReadOnlyList<BankRuleSet> Load(string? directory, List<string> errors, ILogger logger)
    {
        var byId = new Dictionary<string, BankRuleSet>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(BankRuleSetProvider).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".RuleSets.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            Add(byId, stream, name, errors);
        }

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(file);
                Add(byId, stream, Path.GetFileName(file), errors);
            }
        }
        else if (!string.IsNullOrWhiteSpace(directory))
        {
            logger.LogDebug("Bank rule set directory not found; only embedded rule sets are used");
        }

        if (!byId.ContainsKey(GenericId))
        {
            errors.Add("The 'generic' rule set is missing.");
        }

        return byId.Values.OrderBy(r => r.Id == GenericId ? 1 : 0).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static void Add(Dictionary<string, BankRuleSet> byId, Stream stream, string source, List<string> errors)
    {
        BankRuleSet? rs;
        try
        {
            rs = JsonSerializer.Deserialize<BankRuleSet>(stream, Json);
        }
        catch (JsonException)
        {
            errors.Add($"{source}: not valid rule set JSON.");
            return;
        }

        if (rs is null)
        {
            errors.Add($"{source}: empty rule set.");
            return;
        }

        var problems = Validate(rs);
        if (problems.Count > 0)
        {
            errors.AddRange(problems.Select(p => $"{source}: {p}"));
            return;
        }

        byId[rs.Id] = rs;
    }

    /// <summary>Structural validation of one rule set (also used by tests and the admin tooling).</summary>
    public static List<string> Validate(BankRuleSet rs)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rs.Id) || rs.Id.Length > 64)
        {
            errors.Add("id is required (max 64).");
        }

        if (rs.DateFormats is not { Count: > 0 })
        {
            errors.Add("dateFormats must not be empty.");
        }

        if (rs.Amounts is null || string.IsNullOrEmpty(rs.Amounts.DecimalSeparator) || rs.Amounts.DecimalSeparator == rs.Amounts.ThousandsSeparator)
        {
            errors.Add("amounts needs distinct decimal and thousands separators.");
        }

        foreach (var p in rs.DetectPatterns ?? [])
        {
            Compile(p, "detectPatterns", errors);
        }

        if (rs.Header is not null)
        {
            foreach (var (label, pattern) in new[]
            {
                ("header.holderName", rs.Header.HolderName), ("header.bankName", rs.Header.BankName), ("header.accountNumber", rs.Header.AccountNumber),
                ("header.periodFrom", rs.Header.PeriodFrom), ("header.periodTo", rs.Header.PeriodTo),
                ("header.openingBalance", rs.Header.OpeningBalance), ("header.closingBalance", rs.Header.ClosingBalance),
            })
            {
                if (pattern is not null && Compile(pattern, label, errors) is { } rx && !rx.GetGroupNames().Contains("v"))
                {
                    errors.Add($"{label} needs a named group 'v'.");
                }
            }
        }
        else
        {
            errors.Add("header is required.");
        }

        if (rs.Transactions?.Lines is not { Count: > 0 })
        {
            errors.Add("transactions.lines must not be empty.");
        }
        else
        {
            foreach (var p in rs.Transactions.Lines)
            {
                if (Compile(p, "transactions.lines", errors) is { } rx)
                {
                    var groups = rx.GetGroupNames();
                    var hasAmount = groups.Contains("amount") || groups.Contains("debit") || groups.Contains("credit");
                    if (!groups.Contains("date") || !hasAmount)
                    {
                        errors.Add("transactions.lines needs named groups 'date' and 'amount' (or 'debit'/'credit').");
                    }
                }
            }

            foreach (var p in rs.Transactions.IgnoreLines ?? [])
            {
                Compile(p, "transactions.ignoreLines", errors);
            }
        }

        return errors;
    }

    private static System.Text.RegularExpressions.Regex? Compile(string pattern, string label, List<string> errors)
    {
        try
        {
            return TextSupport.Compile(pattern);
        }
        catch (ArgumentException)
        {
            errors.Add($"{label}: invalid regular expression.");
            return null;
        }
    }
}

/// <summary>Reports rule-set problems through <c>ValidateOnStart</c> so a bad file stops the host instead of failing a request.</summary>
public sealed class BankRuleSetsOptionsValidator : IValidateOptions<VerificationOptions>
{
    public ValidateOptionsResult Validate(string? name, VerificationOptions options)
    {
        var errors = new List<string>();
        BankRuleSetProvider.Load(options.BankStatement.RuleSetsPath, errors, NullLogger.Instance);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors.Select(e => "Verification:BankStatement:RuleSets " + e));
    }
}
