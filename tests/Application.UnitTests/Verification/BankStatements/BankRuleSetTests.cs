using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.BankStatements;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.TestSupport.Verification;
using static NexaVerify.Application.Verification.BankStatements.AmountAndDateParsing;

namespace NexaVerify.Application.UnitTests.Verification.BankStatements;

public class BankRuleSetTests
{
    private static readonly AmountFormat Indian = new(".", ",", true, true);

    [Fact]
    public void Embedded_rule_sets_load_and_generic_is_always_present()
    {
        var p = BankStatementParserTests.Provider();
        p.RuleSets.Select(r => r.Id).ShouldContain("generic");
        p.RuleSets.Select(r => r.Id).ShouldBe(p.RuleSets.Select(r => r.Id).Distinct());
        p.RuleSets.Where(r => r.Id != "generic").ShouldAllBe(r => r.DisplayName.Contains("SYNTHETIC", StringComparison.Ordinal));
        p.RuleSets.Count.ShouldBe(4);
    }

    [Fact]
    public void Unknown_layout_falls_back_to_generic()
    {
        var p = BankStatementParserTests.Provider();
        p.Detect(SampleDocuments.TextValue("some other bank", "02/04/2024 X 1.00 2.00")).Id.ShouldBe("generic");
        p.Detect(SampleDocuments.TextValue(SampleTexts.LayoutB())).Id.ShouldBe("synthetic-layout-b");
    }

    [Fact]
    public void Directory_rule_sets_extend_and_invalid_files_fail()
    {
        var dir = Directory.CreateTempSubdirectory("nv-rules-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "custom.json"), """
                { "id":"custom-test","displayName":"custom","detectPatterns":["CUSTOM TEST BANK"],
                  "header":{},"transactions":{"lines":["^(?<date>\\d{2}/\\d{2}/\\d{4})\\s+(?<desc>.+?)\\s+(?<amount>[\\d.,]+)\\s+(?<balance>[\\d.,]+)$"]},
                  "dateFormats":["dd/MM/yyyy"],"amounts":{"decimalSeparator":".","thousandsSeparator":",","lakhGrouping":false,"drCrSuffix":false},"defaultCurrency":"INR"}
                """);
            var errors = new List<string>();
            BankRuleSetProvider.Load(dir, errors, NullLogger.Instance).Select(r => r.Id).ShouldContain("custom-test");
            errors.ShouldBeEmpty();

            File.WriteAllText(Path.Combine(dir, "bad.json"), """{ "id":"bad","header":{},"transactions":{"lines":["(unclosed"]},"dateFormats":["dd/MM/yyyy"],"amounts":{"decimalSeparator":".","thousandsSeparator":","},"detectPatterns":[],"displayName":"x","defaultCurrency":"INR" }""");
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
            var o = new VerificationOptions();
            o.BankStatement.RuleSetsPath = dir;
            var result = new BankRuleSetsOptionsValidator().Validate(null, o);
            result.Failed.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(() => new BankRuleSetProvider(Options.Create(o)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Validation_requires_named_groups()
    {
        var generic = BankStatementParserTests.Provider().RuleSets.First(r => r.Id == "generic");
        BankRuleSetProvider.Validate(generic).ShouldBeEmpty();
        BankRuleSetProvider.Validate(generic with { Transactions = new BankTransactionPatterns(["^(?<desc>.+)$"]) }).ShouldNotBeEmpty();
        BankRuleSetProvider.Validate(generic with { Header = generic.Header with { HolderName = "^(.+)$" } }).ShouldNotBeEmpty();
        BankRuleSetProvider.Validate(generic with { DateFormats = [] }).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("1,500.00", 1500.00, Side.None)]
    [InlineData("1,00,000.00", 100000.00, Side.None)]
    [InlineData("1,500.00 Dr", 1500.00, Side.Debit)]
    [InlineData("250.5Cr", 250.50, Side.Credit)]
    [InlineData("Rs. 10.505", 10.51, Side.None)]
    [InlineData("INR 12.00", 12.00, Side.None)]
    [InlineData("(75.00)", -75.00, Side.None)]
    public void Amounts_parse(string raw, double expected, Side side)
    {
        TryParseAmount(raw, Indian, out var v, out var s).ShouldBeTrue();
        v.ShouldBe((decimal)expected);
        s.ShouldBe(side);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("99999999999999999999999999999999999999.00")]
    public void Bad_amounts_are_rejected(string raw) => TryParseAmount(raw, Indian, out _, out _).ShouldBeFalse();

    [Fact]
    public void Dr_balance_is_negative_and_alternative_separators_work()
    {
        TryParseBalance("100.00 Dr", Indian, out var b).ShouldBeTrue();
        b.ShouldBe(-100m);
        TryParseAmount("1.234,50", new AmountFormat(",", ".", false, false), out var eu, out _).ShouldBeTrue();
        eu.ShouldBe(1234.50m);
    }

    [Fact]
    public void Dates_parse_with_two_digit_years_in_the_2000s()
    {
        TryParseDate("05/04/24", ["dd/MM/yy"], out var d).ShouldBeTrue();
        d.ShouldBe(new DateOnly(2024, 4, 5));
        TryParseDate("05-Apr-2024", ["dd-MMM-yyyy"], out d).ShouldBeTrue();
        TryParseDate("31/02/2024", ["dd/MM/yyyy"], out _).ShouldBeFalse();
        TryParseDate("garbage", ["dd/MM/yyyy"], out _).ShouldBeFalse();
    }
}
