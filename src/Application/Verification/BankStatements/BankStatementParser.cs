using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;
using static NexaVerify.Application.Verification.BankStatements.AmountAndDateParsing;

namespace NexaVerify.Application.Verification.BankStatements;

/// <summary>
/// Rule-set driven statement parser: header fields by labelled patterns, transaction rows by named-group line patterns, multi-line
/// descriptions joined to the preceding row. Pure and deterministic (the only clock is the injected <see cref="TimeProvider"/>). Never
/// logs or exposes content. Checks produced here are REVIEW SIGNALS about the document's internal consistency, not fraud findings.
/// </summary>
public sealed class BankStatementParser : IBankStatementParser
{
    private const int MaxLineLength = 400;
    private const int MaxDescription = 200;

    private readonly VerificationOptions _options;
    private readonly TimeProvider _clock;

    public BankStatementParser(IOptions<VerificationOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public string ParserId => "generic-columnar";

    public BankStatementParseResult Parse(DocumentText text, BankRuleSet rules)
    {
        var lines = TextSupport.Lines(text);
        var tx = new List<Row>();
        var nonTxLines = new List<string>();
        var unparsedDateLines = 0;
        var truncated = false;

        var linePatterns = rules.Transactions.Lines.Select(p => TextSupport.Compile(p)).ToList();
        var ignore = (rules.Transactions.IgnoreLines ?? []).Select(p => TextSupport.Compile(p)).ToList();
        var headerHints = HeaderRegexes(rules).ToList();
        var dateStart = TextSupport.Compile(@"^\d{1,2}[/\-. ][A-Za-z0-9]{1,3}[/\-. ]\d{2,4}\b");
        var hasAmount = TextSupport.Compile(@"\d\.\d{2}\b");

        Row? last = null;
        foreach (var line in lines)
        {
            if (line.Length > MaxLineLength)
            {
                last = null;
                continue;
            }

            var row = TryRow(line, linePatterns, rules);
            if (row is not null)
            {
                if (tx.Count >= _options.BankStatement.MaxTransactions)
                {
                    truncated = true;
                    continue;
                }

                tx.Add(row);
                last = row;
                continue;
            }

            nonTxLines.Add(line); // header and footer fields are read from every non-transaction line
            if (ignore.Any(rx => TextSupport.SafeMatch(rx, line) is not null))
            {
                last = null;
                continue;
            }

            if (TextSupport.SafeMatch(dateStart, line) is not null)
            {
                unparsedDateLines++;
                last = null;
                continue;
            }

            var isHeader = headerHints.Any(rx => TextSupport.SafeMatch(rx, line) is not null);
            if (last is not null && !isHeader && TextSupport.SafeMatch(hasAmount, line) is null && line.Length <= 120)
            {
                last.Description = Truncate(last.Description + " " + TextSupport.Collapse(line));
            }
        }

        var header = ReadHeader(nonTxLines, rules);
        var ordered = Resolve(tx, header.Opening);
        var data = new BankStatementData
        {
            RuleSetId = rules.Id,
            BankName = header.Bank,
            AccountHolderName = header.Holder,
            AccountNumberMasked = header.AccountNumber is null ? null : MaskAccount(header.AccountNumber),
            Currency = string.IsNullOrWhiteSpace(rules.DefaultCurrency) ? _options.BankStatement.DefaultCurrency : rules.DefaultCurrency,
            PeriodFrom = header.From,
            PeriodTo = header.To,
            OpeningBalance = header.Opening,
            ClosingBalance = header.Closing,
            PageCount = text.Pages.Count,
            TextSource = text.Source,
            MeanConfidence = text.MeanConfidence,
            Transactions = ordered.Select((r, i) => new BankTransaction(
                i + 1, r.Date, r.ValueDate, r.Description, r.Reference, r.Debit, r.Credit, r.Balance, r.Confidence)).ToList(),
        };

        var checks = BankStatementChecks.Run(data, _options, _clock.GetUtcNow(), unparsedDateLines, truncated);
        return new BankStatementParseResult(data, checks);
    }

    /// <summary>The raw account number from the header (for the keyed hash only; never stored or logged).</summary>
    public static string? FindAccountNumber(DocumentText text, BankRuleSet rules)
    {
        if (rules.Header.AccountNumber is not { } pattern)
        {
            return null;
        }

        var rx = TextSupport.Compile(pattern);
        foreach (var line in TextSupport.Lines(text))
        {
            if (line.Length <= MaxLineLength && TextSupport.SafeMatch(rx, line) is { } m && m.Groups["v"].Success)
            {
                return m.Groups["v"].Value.Trim();
            }
        }

        return null;
    }

    public static string MaskAccount(string accountNumber)
    {
        var clean = new string(accountNumber.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        var keep = Math.Min(4, clean.Length);
        var masked = new string('X', clean.Length - keep) + clean[^keep..];
        return masked.Length > MaskedIdentifier.MaxLength ? masked[^MaskedIdentifier.MaxLength..] : masked;
    }

    private IEnumerable<Regex> HeaderRegexes(BankRuleSet rules)
    {
        foreach (var p in new[] { rules.Header.HolderName, rules.Header.BankName, rules.Header.AccountNumber, rules.Header.PeriodFrom, rules.Header.PeriodTo, rules.Header.OpeningBalance, rules.Header.ClosingBalance })
        {
            if (p is not null)
            {
                yield return TextSupport.Compile(p);
            }
        }
    }

    private sealed class Row
    {
        public DateOnly Date { get; init; }

        public DateOnly? ValueDate { get; init; }

        public string Description { get; set; } = string.Empty;

        public string? Reference { get; init; }

        public decimal? Debit { get; set; }

        public decimal? Credit { get; set; }

        public decimal? Balance { get; init; }

        public decimal? Confidence { get; init; }

        public decimal? PendingAmount { get; init; }

        public Side PendingSide { get; init; }
    }

    private static Row? TryRow(string line, List<Regex> patterns, BankRuleSet rules)
    {
        foreach (var rx in patterns)
        {
            if (TextSupport.SafeMatch(rx, line) is not { } m)
            {
                continue;
            }

            if (!TryParseDate(Group(m, "date"), rules.DateFormats, out var date))
            {
                continue;
            }

            DateOnly? valueDate = TryParseDate(Group(m, "valueDate"), rules.DateFormats, out var vd) ? vd : null;
            decimal? balance = TryParseBalance(Group(m, "balance"), rules.Amounts, out var b) ? b : null;
            decimal? debit = Money(Group(m, "debit"), rules.Amounts);
            decimal? credit = Money(Group(m, "credit"), rules.Amounts);
            decimal? pending = null;
            var side = Side.None;
            if (Group(m, "amount") is { } rawAmount)
            {
                if (TryParseAmount(rawAmount, rules.Amounts, out var amount, out var s))
                {
                    pending = amount;
                    side = s;
                }
                else
                {
                    continue;
                }
            }

            if (pending is { } p && side != Side.None)
            {
                if (side == Side.Debit)
                {
                    debit = p;
                }
                else
                {
                    credit = p;
                }

                pending = null;
            }

            return new Row
            {
                Date = date,
                ValueDate = valueDate,
                Description = Truncate(TextSupport.Collapse(Group(m, "desc") ?? string.Empty)),
                Reference = Group(m, "ref") is { Length: > 0 } r ? Truncate(r) : null,
                Debit = debit,
                Credit = credit,
                Balance = balance,
                PendingAmount = pending,
                PendingSide = Side.None,
            };
        }

        return null;
    }

    private static decimal? Money(string? raw, AmountFormat format)
    {
        if (raw is null || raw == "-" || !TryParseAmount(raw, format, out var v, out _))
        {
            return null;
        }

        return v == 0m ? null : v;
    }

    private static string? Group(Match m, string name) =>
        m.Groups[name] is { Success: true } g && g.Value.Trim().Length > 0 ? g.Value.Trim() : null;

    private static string Truncate(string value) => value.Length <= MaxDescription ? value : value[..MaxDescription];

    /// <summary>
    /// A row with one amount and no Dr/Cr marker gets its direction from the change in the running balance (walking in the statement's
    /// own chronological direction). The amount stays as printed, so a printed amount that disagrees with the balance change still
    /// shows up in the arithmetic check. Rows that cannot be resolved keep neither side (BS_ROW_SHAPE reports them).
    /// </summary>
    private static List<Row> Resolve(List<Row> rows, decimal? opening)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var descending = rows.Count > 1 && rows[0].Date > rows[^1].Date;
        var chrono = descending ? Enumerable.Reverse(rows).ToList() : rows;
        decimal? prev = opening;
        foreach (var row in chrono)
        {
            if (row.PendingAmount is { } amount && row.Debit is null && row.Credit is null)
            {
                if (prev is { } p && row.Balance is { } bal)
                {
                    if (bal < p)
                    {
                        row.Debit = amount;
                    }
                    else
                    {
                        row.Credit = amount;
                    }
                }
            }

            if (row.Balance is { } b)
            {
                prev = b;
            }
            else if (prev is { } p2)
            {
                prev = p2 + (row.Credit ?? 0m) - (row.Debit ?? 0m);
            }
        }

        return rows;
    }

    private sealed record Header(string? Holder, string? Bank, string? AccountNumber, DateOnly? From, DateOnly? To, decimal? Opening, decimal? Closing);

    private static Header ReadHeader(List<string> lines, BankRuleSet rules)
    {
        string? Find(string? pattern)
        {
            if (pattern is null)
            {
                return null;
            }

            var rx = TextSupport.Compile(pattern);
            foreach (var line in lines)
            {
                if (line.Length <= MaxLineLength && TextSupport.SafeMatch(rx, line) is { } m && m.Groups["v"].Success)
                {
                    var v = m.Groups["v"].Value.Trim();
                    if (v.Length > 0)
                    {
                        return v;
                    }
                }
            }

            return null;
        }

        DateOnly? Date(string? raw) => TryParseDate(raw, rules.DateFormats, out var d) ? d : null;
        decimal? Balance(string? raw) => TryParseBalance(raw, rules.Amounts, out var v) ? v : null;

        var holder = Find(rules.Header.HolderName);
        var bank = Find(rules.Header.BankName);
        return new Header(
            holder is null ? null : TextSupport.Collapse(holder).ToUpperInvariant(),
            bank is null ? null : TextSupport.Collapse(bank),
            Find(rules.Header.AccountNumber),
            Date(Find(rules.Header.PeriodFrom)),
            Date(Find(rules.Header.PeriodTo)),
            Balance(Find(rules.Header.OpeningBalance)),
            Balance(Find(rules.Header.ClosingBalance)));
    }
}
