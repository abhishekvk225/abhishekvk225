using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.BankStatements;

/// <summary>Stable codes of the bank statement checks (docs/07 §6.4).</summary>
public static class BankCheckCodes
{
    public const string Completeness = "BS_COMPLETENESS";
    public const string PeriodValid = "BS_PERIOD_VALID";
    public const string RowShape = "BS_ROW_SHAPE";
    public const string TxInPeriod = "BS_TX_IN_PERIOD";
    public const string TxOrder = "BS_TX_ORDER";
    public const string RowArithmetic = "BS_ROW_ARITHMETIC";
    public const string OpeningContinuity = "BS_OPENING_CONTINUITY";
    public const string ClosingContinuity = "BS_CLOSING_CONTINUITY";
    public const string Totals = "BS_TOTALS";
    public const string Duplicates = "BS_DUPLICATES";
    public const string SubmittedMatch = "BS_SUBMITTED_MATCH";
    public const string ExtractionConfidence = "BS_EXTRACTION_CONFIDENCE";
    public const string ExternalNotPerformed = "BS_EXTERNAL_NOT_PERFORMED";
}

/// <summary>
/// The consistency checks of a normalised statement. A failed check is a REVIEW SIGNAL ("inconsistent", "could not be reconciled"); the
/// wording never says forged, fake or tampered. Messages carry counts and row indices only, never values. A check that cannot run
/// because data is missing is NotEvaluated, never silently skipped and never Pass.
/// </summary>
public static class BankStatementChecks
{
    public static List<CheckResult> Run(BankStatementData d, VerificationOptions options, DateTimeOffset now, int unparsedDateLines, bool truncated)
    {
        var tol = options.BankStatement.BalanceTolerance;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var rows = d.Transactions;
        var descending = rows.Count > 1 && rows[0].Date > rows[^1].Date;
        var chrono = descending ? rows.Reverse().ToList() : rows.ToList();

        return
        [
            Completeness(d),
            Period(d, options, today),
            RowShape(rows),
            Confidence(d, options, unparsedDateLines, truncated),
            InPeriod(d),
            Order(rows),
            Arithmetic(d, chrono, tol),
            Opening(d, chrono, tol),
            Closing(d, chrono, tol),
            Totals(d, rows, tol),
            Duplicates(rows),
        ];
    }

    public static CheckResult ExternalNotPerformed() => new(
        BankCheckCodes.ExternalNotPerformed, CheckStage.External, CheckOutcome.NotApplicable, CheckSeverity.Info, null,
        "No authoritative statement verification is performed for bank statements.");

    private static CheckResult C(string code, CheckStage stage, CheckOutcome outcome, CheckSeverity severity, string message, string? field = null) =>
        new(code, stage, outcome, severity, field, message);

    private static CheckResult Completeness(BankStatementData d)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(d.AccountHolderName)) { missing.Add("account holder"); }
        if (string.IsNullOrWhiteSpace(d.BankName)) { missing.Add("bank name"); }
        if (string.IsNullOrWhiteSpace(d.AccountNumberMasked)) { missing.Add("account number"); }
        if (d.PeriodFrom is null || d.PeriodTo is null) { missing.Add("statement period"); }
        if (d.OpeningBalance is null) { missing.Add("opening balance"); }
        if (d.ClosingBalance is null) { missing.Add("closing balance"); }
        if (d.Transactions.Count == 0) { missing.Add("transactions"); }

        return missing.Count == 0
            ? C(BankCheckCodes.Completeness, CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "All expected statement fields were found.")
            : C(BankCheckCodes.Completeness, CheckStage.Rules, d.Transactions.Count == 0 ? CheckOutcome.Fail : CheckOutcome.Warn, CheckSeverity.Review,
                "Not found in the document: " + string.Join(", ", missing) + ".");
    }

    private static CheckResult Period(BankStatementData d, VerificationOptions o, DateOnly today)
    {
        if (d.PeriodFrom is not { } from || d.PeriodTo is not { } to)
        {
            return C(BankCheckCodes.PeriodValid, CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, "The statement period was not found.");
        }

        if (from > to)
        {
            return C(BankCheckCodes.PeriodValid, CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "The period start is after the period end.");
        }

        if (to > today)
        {
            return C(BankCheckCodes.PeriodValid, CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review, "The period ends in the future.");
        }

        return to.DayNumber - from.DayNumber + 1 > o.BankStatement.MaxPeriodDays
            ? C(BankCheckCodes.PeriodValid, CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Review, "The period is longer than the configured maximum.")
            : C(BankCheckCodes.PeriodValid, CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "The statement period is plausible.");
    }

    private static CheckResult RowShape(IReadOnlyList<BankTransaction> rows)
    {
        if (rows.Count == 0)
        {
            return C(BankCheckCodes.RowShape, CheckStage.Rules, CheckOutcome.NotEvaluated, CheckSeverity.Info, "No transactions were recognised.");
        }

        var bad = rows.Where(r => (r.Debit is null) == (r.Credit is null)).Select(r => r.Index).ToList();
        return bad.Count == 0
            ? C(BankCheckCodes.RowShape, CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "Every row has a date and exactly one of debit or credit.")
            : C(BankCheckCodes.RowShape, CheckStage.Rules, CheckOutcome.Fail, CheckSeverity.Review,
                $"{bad.Count} row(s) have both or neither of debit and credit (first at row {bad[0]}).");
    }

    private static CheckResult Confidence(BankStatementData d, VerificationOptions o, int unparsed, bool truncated)
    {
        var notes = new List<string>();
        if (d.MeanConfidence is { } c && c < o.Ocr.MinMeanConfidence)
        {
            notes.Add("text recognition confidence is low");
        }

        if (unparsed > 0)
        {
            notes.Add($"{unparsed} line(s) starting with a date could not be read as transactions");
        }

        if (truncated)
        {
            notes.Add("the transaction limit was reached");
        }

        return notes.Count == 0
            ? C(BankCheckCodes.ExtractionConfidence, CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "Extraction quality looks adequate.")
            : C(BankCheckCodes.ExtractionConfidence, CheckStage.Rules, CheckOutcome.Warn, CheckSeverity.Review, "Extraction may be incomplete: " + string.Join("; ", notes) + ".");
    }

    private static CheckResult InPeriod(BankStatementData d)
    {
        if (d.PeriodFrom is not { } from || d.PeriodTo is not { } to || d.Transactions.Count == 0)
        {
            return C(BankCheckCodes.TxInPeriod, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "The period or the transactions are missing.");
        }

        var outside = d.Transactions.Count(t => t.Date < from || t.Date > to);
        return outside == 0
            ? C(BankCheckCodes.TxInPeriod, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "All transactions fall inside the statement period.")
            : C(BankCheckCodes.TxInPeriod, CheckStage.Consistency, CheckOutcome.Fail, CheckSeverity.Review, $"{outside} transaction(s) fall outside the statement period.");
    }

    private static CheckResult Order(IReadOnlyList<BankTransaction> rows)
    {
        if (rows.Count < 2)
        {
            return C(BankCheckCodes.TxOrder, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "Fewer than two transactions.");
        }

        var asc = true;
        var desc = true;
        int? ascBreak = null, descBreak = null;
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].Date < rows[i - 1].Date)
            {
                asc = false;
                ascBreak ??= rows[i].Index;
            }

            if (rows[i].Date > rows[i - 1].Date)
            {
                desc = false;
                descBreak ??= rows[i].Index;
            }
        }

        return asc || desc
            ? C(BankCheckCodes.TxOrder, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "Transaction dates are in a consistent order.")
            : C(BankCheckCodes.TxOrder, CheckStage.Consistency, CheckOutcome.Warn, CheckSeverity.Warning,
                $"Transaction dates are not in a single order (first break at row {Math.Max(ascBreak ?? 0, descBreak ?? 0)}).");
    }

    private static bool Differs(decimal a, decimal b, decimal tol) => Math.Abs(a - b) > tol;

    private static CheckResult Arithmetic(BankStatementData d, List<BankTransaction> chrono, decimal tol)
    {
        decimal? prev = d.OpeningBalance;
        var evaluated = 0;
        var failed = 0;
        int? first = null;
        foreach (var r in chrono)
        {
            if (prev is { } p && r.Balance is { } bal && (r.Debit is not null) != (r.Credit is not null))
            {
                evaluated++;
                if (Differs(p + (r.Credit ?? 0m) - (r.Debit ?? 0m), bal, tol))
                {
                    failed++;
                    first ??= r.Index;
                }
            }

            prev = r.Balance;
        }

        if (evaluated == 0)
        {
            return C(BankCheckCodes.RowArithmetic, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "Running balances are not available.");
        }

        return failed == 0
            ? C(BankCheckCodes.RowArithmetic, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, $"Running balance reconciles for {evaluated} row(s).")
            : C(BankCheckCodes.RowArithmetic, CheckStage.Consistency, CheckOutcome.Fail, CheckSeverity.Review,
                $"{failed} of {evaluated} row(s) do not reconcile with the running balance (first at row {first}).");
    }

    private static CheckResult Opening(BankStatementData d, List<BankTransaction> chrono, decimal tol)
    {
        var first = chrono.FirstOrDefault();
        if (d.OpeningBalance is not { } opening || first is null || first.Balance is not { } bal || (first.Debit is null) == (first.Credit is null))
        {
            return C(BankCheckCodes.OpeningContinuity, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "Opening balance or first running balance is not available.");
        }

        return Differs(opening + (first.Credit ?? 0m) - (first.Debit ?? 0m), bal, tol)
            ? C(BankCheckCodes.OpeningContinuity, CheckStage.Consistency, CheckOutcome.Fail, CheckSeverity.Review, "The opening balance does not lead to the first running balance.")
            : C(BankCheckCodes.OpeningContinuity, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "The opening balance leads to the first running balance.");
    }

    private static CheckResult Closing(BankStatementData d, List<BankTransaction> chrono, decimal tol)
    {
        var last = chrono.LastOrDefault();
        if (d.ClosingBalance is not { } closing || last?.Balance is not { } bal)
        {
            return C(BankCheckCodes.ClosingContinuity, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "Closing balance or last running balance is not available.");
        }

        return Differs(closing, bal, tol)
            ? C(BankCheckCodes.ClosingContinuity, CheckStage.Consistency, CheckOutcome.Fail, CheckSeverity.Review, "The last running balance differs from the closing balance.")
            : C(BankCheckCodes.ClosingContinuity, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "The last running balance equals the closing balance.");
    }

    private static CheckResult Totals(BankStatementData d, IReadOnlyList<BankTransaction> rows, decimal tol)
    {
        if (d.OpeningBalance is not { } o || d.ClosingBalance is not { } c || rows.Count == 0 || rows.Any(r => (r.Debit is null) == (r.Credit is null)))
        {
            return C(BankCheckCodes.Totals, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "Balances or transaction directions are not fully available.");
        }

        var expected = o + rows.Sum(r => r.Credit ?? 0m) - rows.Sum(r => r.Debit ?? 0m);
        return Differs(expected, c, tol)
            ? C(BankCheckCodes.Totals, CheckStage.Consistency, CheckOutcome.Fail, CheckSeverity.Review, "Opening balance plus credits minus debits does not equal the closing balance.")
            : C(BankCheckCodes.Totals, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "Opening balance plus credits minus debits equals the closing balance.");
    }

    private static CheckResult Duplicates(IReadOnlyList<BankTransaction> rows)
    {
        if (rows.Count < 2)
        {
            return C(BankCheckCodes.Duplicates, CheckStage.Consistency, CheckOutcome.NotEvaluated, CheckSeverity.Info, "Fewer than two transactions.");
        }

        var withBalance = rows.Where(r => r.Balance is not null)
            .GroupBy(r => (r.Date, r.Description, r.Debit, r.Credit, r.Balance)).Count(g => g.Count() > 1);
        var withoutBalance = rows.Where(r => r.Balance is null)
            .GroupBy(r => (r.Date, r.Description, r.Debit, r.Credit)).Count(g => g.Count() > 1);

        if (withBalance > 0)
        {
            return C(BankCheckCodes.Duplicates, CheckStage.Consistency, CheckOutcome.Warn, CheckSeverity.Warning, $"{withBalance} group(s) of identical rows including the running balance.");
        }

        return withoutBalance > 0
            ? C(BankCheckCodes.Duplicates, CheckStage.Consistency, CheckOutcome.Warn, CheckSeverity.Info, $"{withoutBalance} group(s) of identical rows (repeated payments are legitimate).")
            : C(BankCheckCodes.Duplicates, CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "No duplicate rows were found.");
    }
}
