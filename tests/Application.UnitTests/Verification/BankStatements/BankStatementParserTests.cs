using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.BankStatements;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Application.UnitTests.Verification.BankStatements;

public class BankStatementParserTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static VerificationOptions Opts() => new();

    internal static BankRuleSetProvider Provider() => new(Options.Create(Opts()));

    internal static BankStatementParser Parser(VerificationOptions? o = null) => new(Options.Create(o ?? Opts()), new FixedClock(Now));

    internal static BankStatementParseResult Parse(string[] lines, string? ruleSet = null, VerificationOptions? o = null)
    {
        var provider = Provider();
        var text = SampleDocuments.TextValue(lines);
        var rules = ruleSet is null ? provider.Detect(text) : provider.RuleSets.First(r => r.Id == ruleSet);
        return Parser(o).Parse(text, rules);
    }

    internal static CheckResult Check(BankStatementParseResult r, string code) => r.Checks.Single(c => c.Code == code);

    [Fact]
    public void Generic_statement_is_parsed_and_every_check_passes()
    {
        var r = Parse(SampleTexts.GenericStatement());
        var d = r.Data;
        d.RuleSetId.ShouldBe("generic");
        d.AccountHolderName.ShouldBe(SampleTexts.Holder);
        d.BankName.ShouldBe("Synthetic Test Bank");
        d.AccountNumberMasked.ShouldBe("XXXXXX2233");
        d.PeriodFrom.ShouldBe(new DateOnly(2024, 4, 1));
        d.PeriodTo.ShouldBe(new DateOnly(2024, 4, 30));
        d.OpeningBalance.ShouldBe(10000m);
        d.ClosingBalance.ShouldBe(20500m);
        d.Transactions.Count.ShouldBe(4);
        d.Transactions[0].Debit.ShouldBe(500m);
        d.Transactions[1].Credit.ShouldBe(25000m);
        d.Transactions[2].Description.ShouldBe("ATM WITHDRAWAL continued narration line"); // multi-line description joined
        d.Transactions.Select(t => t.Index).ShouldBe([1, 2, 3, 4]);

        r.Checks.Where(c => c.Outcome is CheckOutcome.Fail or CheckOutcome.Warn).ShouldBeEmpty();
        r.Checks.Count(c => c.Outcome == CheckOutcome.Pass).ShouldBe(11);
    }

    [Theory]
    [InlineData("synthetic-layout-a")]
    [InlineData("synthetic-layout-b")]
    [InlineData("synthetic-layout-c")]
    public void Synthetic_layouts_are_detected_parsed_and_reconcile(string id)
    {
        var lines = id switch
        {
            "synthetic-layout-a" => SampleTexts.LayoutA(),
            "synthetic-layout-b" => SampleTexts.LayoutB(),
            _ => SampleTexts.LayoutC(),
        };
        var r = Parse(lines);
        r.Data.RuleSetId.ShouldBe(id);
        r.Data.AccountHolderName.ShouldBe(SampleTexts.Holder);
        r.Data.Transactions.Count.ShouldBe(2);
        r.Data.PeriodFrom.ShouldBe(new DateOnly(2024, 4, 1));
        r.Checks.Where(c => c.Outcome is CheckOutcome.Fail or CheckOutcome.Warn).ShouldBeEmpty(string.Join(",", r.Checks.Where(c => c.Outcome is CheckOutcome.Fail or CheckOutcome.Warn).Select(c => c.Code)));
        Check(r, BankCheckCodes.Totals).Outcome.ShouldBe(CheckOutcome.Pass);
    }

    [Fact]
    public void Lakh_grouping_is_parsed()
    {
        var r = Parse(SampleTexts.LayoutC());
        r.Data.OpeningBalance.ShouldBe(100000m);
        r.Data.Transactions[1].Credit.ShouldBe(250000m);
        r.Data.Transactions[1].Balance.ShouldBe(345000m);
    }

    [Fact]
    public void Descending_statement_is_reconciled_direction_aware()
    {
        var lines = SampleTexts.GenericStatement().Where(l => !char.IsDigit(l[0]) || !l.Contains("/04/2024 ")).ToList();
        var rows = SampleTexts.GenericStatement().Where(l => l.Length > 10 && char.IsDigit(l[0]) && l[2] == '/').Reverse().ToList();
        var shuffled = new List<string>(SampleTexts.GenericStatement().Take(7)); // header up to "Date Narration..."
        shuffled.AddRange(rows.Take(1));
        shuffled.AddRange(rows.Skip(1));
        shuffled.Add("Closing Balance: 20,500.00");
        var r = Parse([.. shuffled]);
        Check(r, BankCheckCodes.TxOrder).Outcome.ShouldBe(CheckOutcome.Pass);
        Check(r, BankCheckCodes.RowArithmetic).Outcome.ShouldBe(CheckOutcome.Pass);
        Check(r, BankCheckCodes.Totals).Outcome.ShouldBe(CheckOutcome.Pass);
        _ = lines;
    }

    [Fact]
    public void Arithmetic_error_is_flagged_with_the_row_index_and_no_value()
    {
        var lines = SampleTexts.GenericStatement().Select(l => l.Replace("32,500.00", "32,900.00")).ToArray();
        var r = Parse(lines);
        var c = Check(r, BankCheckCodes.RowArithmetic);
        c.Outcome.ShouldBe(CheckOutcome.Fail);
        c.Severity.ShouldBe(CheckSeverity.Review);
        c.Message.ShouldContain("row 3");
        c.Message.ShouldNotContain("32,900");
        Check(r, BankCheckCodes.Totals).Outcome.ShouldBe(CheckOutcome.Pass); // totals use the printed amounts
        Check(r, BankCheckCodes.ClosingContinuity).Outcome.ShouldBe(CheckOutcome.Pass);
    }

    [Fact]
    public void Wrong_closing_balance_fails_continuity_and_totals()
    {
        var r = Parse(SampleTexts.GenericStatement().Select(l => l.Replace("Closing Balance: 20,500.00", "Closing Balance: 21,500.00")).ToArray());
        Check(r, BankCheckCodes.ClosingContinuity).Outcome.ShouldBe(CheckOutcome.Fail);
        Check(r, BankCheckCodes.Totals).Outcome.ShouldBe(CheckOutcome.Fail);
    }

    [Fact]
    public void Wrong_opening_balance_fails_opening_continuity()
    {
        var r = Parse(SampleTexts.GenericStatement().Select(l => l.Replace("Opening Balance: 10,000.00", "Opening Balance: 11,000.00")).ToArray());
        Check(r, BankCheckCodes.OpeningContinuity).Outcome.ShouldBe(CheckOutcome.Fail);
    }

    [Fact]
    public void Transaction_outside_the_period_is_flagged()
    {
        var r = Parse(SampleTexts.GenericStatement().Select(l => l.Replace("20/04/2024 RENT", "05/06/2024 RENT")).ToArray());
        var c = Check(r, BankCheckCodes.TxInPeriod);
        c.Outcome.ShouldBe(CheckOutcome.Fail);
        c.Message.ShouldContain("1 transaction");
    }

    [Fact]
    public void Mixed_order_is_a_warning_with_the_first_break()
    {
        var r = Parse(SampleTexts.GenericStatement().Select(l => l.Replace("10/04/2024 ATM", "03/04/2024 ATM")).ToArray());
        var c = Check(r, BankCheckCodes.TxOrder);
        c.Outcome.ShouldBe(CheckOutcome.Warn);
        c.Severity.ShouldBe(CheckSeverity.Warning);
    }

    [Fact]
    public void Duplicate_rows_with_balances_warn_and_without_balances_are_informational()
    {
        // Explicit Dr/Cr markers (layout A) so a repeated row keeps its direction and its balance.
        var dup = SampleTexts.LayoutA().ToList();
        var i = dup.FindIndex(l => l.StartsWith("02-Apr-2024", StringComparison.Ordinal));
        dup.Insert(i + 1, dup[i]);
        var r = Parse([.. dup]);
        r.Data.Transactions.Count.ShouldBe(3);
        Check(r, BankCheckCodes.Duplicates).Outcome.ShouldBe(CheckOutcome.Warn);
        Check(r, BankCheckCodes.Duplicates).Severity.ShouldBe(CheckSeverity.Warning);

        // Layout A requires a balance; use the debit/credit layout B with a "-" balance-less variant through the check itself.
        var rows = new[]
        {
            new BankTransaction(1, new DateOnly(2024, 4, 2), null, "PAYMENT", null, 100m, null, null, null),
            new BankTransaction(2, new DateOnly(2024, 4, 2), null, "PAYMENT", null, 100m, null, null, null),
        };
        var data = r.Data with { Transactions = rows };
        var checks = BankStatementChecks.Run(data, Opts(), Now, 0, false);
        var c = checks.Single(x => x.Code == BankCheckCodes.Duplicates);
        c.Outcome.ShouldBe(CheckOutcome.Warn);
        c.Severity.ShouldBe(CheckSeverity.Info);
        BankStatementChecks.Run(r.Data with { Transactions = [rows[0]] }, Opts(), Now, 0, false)
            .Single(x => x.Code == BankCheckCodes.Duplicates).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
    }

    [Fact]
    public void Period_checks()
    {
        Check(Parse(SampleTexts.GenericStatement().Select(l => l.Replace("01/04/2024 to 30/04/2024", "30/04/2024 to 01/04/2024")).ToArray()), BankCheckCodes.PeriodValid)
            .Outcome.ShouldBe(CheckOutcome.Fail);
        Check(Parse(SampleTexts.GenericStatement().Select(l => l.Replace("01/04/2024 to 30/04/2024", "01/04/2024 to 30/04/2030")).ToArray()), BankCheckCodes.PeriodValid)
            .Outcome.ShouldBe(CheckOutcome.Fail); // ends in the future
        Check(Parse(SampleTexts.GenericStatement().Select(l => l.Replace("01/04/2024 to 30/04/2024", "01/01/2022 to 30/04/2024")).ToArray()), BankCheckCodes.PeriodValid)
            .Outcome.ShouldBe(CheckOutcome.Warn); // longer than MaxPeriodDays
    }

    [Fact]
    public void Missing_data_is_not_evaluated_never_pass()
    {
        var r = Parse(["02/04/2024 PAYMENT 100.00 900.00"]);
        Check(r, BankCheckCodes.PeriodValid).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Check(r, BankCheckCodes.OpeningContinuity).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Check(r, BankCheckCodes.ClosingContinuity).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Check(r, BankCheckCodes.Totals).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Check(r, BankCheckCodes.TxInPeriod).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Check(r, BankCheckCodes.TxOrder).Outcome.ShouldBe(CheckOutcome.NotEvaluated);
        Check(r, BankCheckCodes.Completeness).Outcome.ShouldBe(CheckOutcome.Warn);
        Check(r, BankCheckCodes.RowShape).Outcome.ShouldBe(CheckOutcome.Fail); // direction cannot be inferred without any prior balance
    }

    [Fact]
    public void Unreadable_date_led_lines_and_low_confidence_raise_a_review_signal()
    {
        var lines = SampleTexts.GenericStatement().Append("25/04/2024 GARBLED ROW WITHOUT AMOUNTS").ToArray();
        var r = Parse(lines);
        var c = Check(r, BankCheckCodes.ExtractionConfidence);
        c.Outcome.ShouldBe(CheckOutcome.Warn);
        c.Message.ShouldContain("1 line");

        var text = new DocumentText([new PageText(1, SampleTexts.GenericStatement().Select(l => new TextLine(l, 0.3m, null, null, null, null)).ToList())], TextSource.Ocr, 0.3m);
        var low = Parser().Parse(text, Provider().RuleSets.First(x => x.Id == "generic"));
        Check(low, BankCheckCodes.ExtractionConfidence).Outcome.ShouldBe(CheckOutcome.Warn);
    }

    [Fact]
    public void Transaction_limit_is_enforced_and_reported()
    {
        var o = Opts();
        o.BankStatement.MaxTransactions = 2;
        var r = Parse(SampleTexts.GenericStatement(), o: o);
        r.Data.Transactions.Count.ShouldBe(2);
        Check(r, BankCheckCodes.ExtractionConfidence).Message.ShouldContain("limit");
    }

    [Fact]
    public void Wording_never_claims_fraud_or_authenticity()
    {
        var r = Parse(SampleTexts.GenericStatement().Select(l => l.Replace("32,500.00", "99,999.00")).ToArray());
        foreach (var c in r.Checks)
        {
            foreach (var word in new[] { "forged", "fake", "tamper", "fraud", "authentic", "genuine" })
            {
                c.Message.ShouldNotContain(word, Case.Insensitive);
            }
        }
    }

    [Fact]
    public void Hostile_input_does_not_hang_or_throw()
    {
        var evil = new string(' ', 399) + "1";
        var longLine = "02/04/2024 " + string.Concat(Enumerable.Repeat("a 1.00 ", 200)) + "1.00 1.00";
        Should.NotThrow(() => Parse([evil, longLine, "", "\0\0\0", "02/04/2024 " + new string('9', 300) + ".00 1.00"]));
    }

    [Fact]
    public void Mask_and_find_account_number()
    {
        BankStatementParser.MaskAccount("9900 1122 33").ShouldBe("XXXXXX2233");
        BankStatementParser.MaskAccount("12").ShouldBe("12");
        BankStatementParser.MaskAccount(string.Empty).ShouldBe(string.Empty);
        var p = Provider();
        var text = SampleDocuments.TextValue(SampleTexts.GenericStatement());
        BankStatementParser.FindAccountNumber(text, p.Detect(text)).ShouldBe(SampleTexts.AccountNumber);
    }

    [Fact]
    public void Data_record_does_not_print_its_content() =>
        Parse(SampleTexts.GenericStatement()).Data.ToString().ShouldNotContain("TESTPERSON");
}
