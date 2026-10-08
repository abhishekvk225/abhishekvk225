using NexaVerify.Application.Dashboards;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.UnitTests;

public class CsvFormatTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+SUM(A1)", "'+SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("\r=1", "\"'\r=1\"")]
    [InlineData("   =HYPERLINK(\"x\")", "\"'   =HYPERLINK(\"\"x\"\")\"")]
    [InlineData("\u200B=1+1", "'\u200B=1+1")]
    [InlineData("\uFEFF@cmd", "'\uFEFF@cmd")]
    public void Cells_that_a_spreadsheet_would_run_as_formulas_are_neutralised(string input, string expected) =>
        CsvFormat.Text(input).ShouldBe(expected);

    [Theory]
    [InlineData("Acme Ltd", "Acme Ltd")]
    [InlineData("a=b", "a=b")]
    [InlineData("x-ray", "x-ray")]
    [InlineData("", "")]
    public void Ordinary_text_is_left_alone(string input, string expected) => CsvFormat.Text(input).ShouldBe(expected);

    [Fact]
    public void Commas_quotes_and_line_breaks_are_quoted()
    {
        CsvFormat.Text("a,b").ShouldBe("\"a,b\"");
        CsvFormat.Text("say \"hi\"").ShouldBe("\"say \"\"hi\"\"\"");
        CsvFormat.Text("two\nlines").ShouldBe("\"two\nlines\"");
    }

    [Fact]
    public void Numbers_dates_and_nulls_are_written_in_an_invariant_format_and_never_prefixed()
    {
        CsvFormat.Line(new DateOnly(2026, 3, 9), "Verify", -5L, 12, null, "=evil").ShouldBe("2026-03-09,Verify,-5,12,,'=evil\r\n");
    }

    [Fact]
    public void An_unsupported_cell_type_is_a_programming_error() =>
        Should.Throw<ArgumentException>(() => CsvFormat.Line(1.5m));
}

public class ReportRangeTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    [Fact]
    public void Without_bounds_the_report_covers_the_default_number_of_days_ending_today()
    {
        var range = ReportRange.Resolve(new UsageReportQuery(), Today, new DashboardOptions { DefaultReportDays = 30 });
        range.Value.To.ShouldBe(Today);
        range.Value.From.ShouldBe(Today.AddDays(-29));
    }

    [Fact]
    public void The_longest_allowed_range_is_accepted_and_one_day_more_is_not()
    {
        var options = new DashboardOptions { MaxReportDays = 92 };
        ReportRange.Resolve(new UsageReportQuery { From = Today.AddDays(-91), To = Today }, Today, options).IsSuccess.ShouldBeTrue();

        var tooLong = ReportRange.Resolve(new UsageReportQuery { From = Today.AddDays(-92), To = Today }, Today, options);
        tooLong.IsFailure.ShouldBeTrue();
        tooLong.Error!.FieldErrors!.ShouldContainKey("to");
    }

    [Fact]
    public void A_reversed_range_is_rejected() =>
        ReportRange.Resolve(new UsageReportQuery { From = Today, To = Today.AddDays(-1) }, Today, new DashboardOptions()).IsFailure.ShouldBeTrue();

    [Fact]
    public void A_start_date_alone_is_measured_up_to_today()
    {
        var options = new DashboardOptions { MaxReportDays = 92 };
        ReportRange.Resolve(new UsageReportQuery { From = Today.AddDays(-200) }, Today, options).IsFailure.ShouldBeTrue();
    }
}

public class DashboardMathTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void The_window_is_whole_utc_days_ending_with_today()
    {
        var (from, to) = DashboardMath.Window(Now, 7);
        from.ShouldBe(new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc));
        to.ShouldBe(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        DashboardMath.Days(from, to).Count.ShouldBe(7);
    }

    [Theory]
    [InlineData(RecognitionOutcome.Enrolled, OutcomeClass.Successful)]
    [InlineData(RecognitionOutcome.Matched, OutcomeClass.Successful)]
    [InlineData(RecognitionOutcome.NoMatch, OutcomeClass.NoMatch)]
    [InlineData(RecognitionOutcome.NoFaceDetected, OutcomeClass.Failed)]
    [InlineData(RecognitionOutcome.MultipleFaces, OutcomeClass.Failed)]
    [InlineData(RecognitionOutcome.LowQuality, OutcomeClass.Failed)]
    [InlineData(RecognitionOutcome.ProviderError, OutcomeClass.Failed)]
    [InlineData(RecognitionOutcome.Rejected, OutcomeClass.Failed)]
    public void Outcomes_fall_into_successful_no_match_or_failed(RecognitionOutcome outcome, OutcomeClass expected) =>
        DashboardMath.Classify(outcome).ShouldBe(expected);

    [Fact]
    public void Every_outcome_is_classified() =>
        Enum.GetValues<RecognitionOutcome>().ShouldAllBe(o => Enum.IsDefined(DashboardMath.Classify(o)));

    [Fact]
    public void Rates_are_shares_rounded_to_four_places_and_zero_when_empty()
    {
        DashboardMath.Rate(1, 3).ShouldBe(0.3333m);
        DashboardMath.Rate(0, 0).ShouldBe(0m);
        DashboardMath.Rate(5, 0).ShouldBe(0m);
    }

    [Fact]
    public void The_percentile_is_read_from_the_histogram_as_the_upper_edge_of_its_bucket()
    {
        // 100 requests: 94 in [0,25), 4 in [25,50), 2 in [200,225)
        var histogram = new[] { (0, 94L), (1, 4L), (8, 2L) };
        DashboardMath.Percentile(histogram, 0.95, 25).ShouldBe(50); // the 95th request is the 1st of the second bucket
        DashboardMath.Percentile(histogram, 0.99, 25).ShouldBe(225);
        DashboardMath.Percentile(histogram, 0.50, 25).ShouldBe(25);
        DashboardMath.Percentile([], 0.95, 25).ShouldBeNull();
        DashboardMath.Percentile([(3, 0L)], 0.95, 25).ShouldBeNull();
    }

    [Fact]
    public void Recognitions_per_day_are_zero_filled_and_split_by_outcome_class()
    {
        var d1 = new DateTime(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new RecognitionDayRow(d1, MeteredOperation.Verify, RecognitionOutcome.Matched, 6),
            new RecognitionDayRow(d1, MeteredOperation.Verify, RecognitionOutcome.NoMatch, 3),
            new RecognitionDayRow(d1, MeteredOperation.Enroll, RecognitionOutcome.Enrolled, 1),
            new RecognitionDayRow(d1, MeteredOperation.Identify, RecognitionOutcome.ProviderError, 2),
        };
        var days = DashboardMath.Days(d1.AddDays(-1), d1.AddDays(2));

        var perDay = DashboardMath.RecognitionsPerDay(days, rows);

        perDay.Select(d => d.Total).ShouldBe([0L, 12L, 0L]);
        perDay[1].ShouldBe(new RecognitionDayDto(DateOnly.FromDateTime(d1), 12, 7, 3, 2));
        var summary = DashboardMath.Summarise(perDay);
        (summary.Successful, summary.NoMatch, summary.Failed).ShouldBe((7L, 3L, 2L));
        summary.SuccessRate.ShouldBe(0.5833m);
        summary.ErrorRate.ShouldBe(0.1667m);
    }

    [Fact]
    public void Credits_per_day_come_from_signed_ledger_amounts_and_net_off_refunds()
    {
        var d = new DateTime(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new LedgerDayRow(d, LedgerEntryType.Consume, MeteredOperation.Verify, 3, -3),
            new LedgerDayRow(d, LedgerEntryType.Consume, MeteredOperation.Identify, 2, -4),
            new LedgerDayRow(d, LedgerEntryType.Refund, MeteredOperation.Verify, 1, 1),
        };

        var perDay = DashboardMath.CreditsPerDay(DashboardMath.Days(d, d.AddDays(1)), rows);

        perDay.Single().ShouldBe(new CreditDayDto(DateOnly.FromDateTime(d), 7, 1, 6));
        DashboardMath.Summarise(perDay).ShouldBe(new CreditSummaryDto(7, 1, 6));
    }

    [Fact]
    public void Licenses_past_their_end_date_count_as_expired_whatever_their_stored_status()
    {
        var rows = new[]
        {
            new LicenseStatusCount(LicenseStatus.Active, false, 5),
            new LicenseStatusCount(LicenseStatus.Active, true, 2),
            new LicenseStatusCount(LicenseStatus.Expired, true, 3),
            new LicenseStatusCount(LicenseStatus.Suspended, true, 1),
            new LicenseStatusCount(LicenseStatus.Revoked, true, 4),
        };

        var counts = DashboardMath.EffectiveStatusCounts(rows, Now).ToDictionary(c => c.Label, c => c.Count);

        counts.ShouldBe(new Dictionary<string, long> { ["Active"] = 5, ["Expired"] = 6, ["Revoked"] = 4 });
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 91, 9)]
    [InlineData(100, 100, 0)]
    [InlineData(0, 0, 0)]
    public void Percent_remaining_rounds_down(int total, int consumed, int expected) =>
        DashboardMath.PercentRemaining(total, consumed).ShouldBe(expected);
}
