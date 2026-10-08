using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>The usage CSV exports: content, range limits, CSV-injection safety, tenant scoping, permissions and audit.</summary>
[Collection(SqlServerCollection.Name)]
public class UsageReportTests : UsageTestBase
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public UsageReportTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private async Task<(HttpResponseMessage Response, string Body)> CsvAsync(string path, string? token)
    {
        var response = await App.GetAsync(path, token);
        return (response, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_client_export_is_a_streamed_csv_of_its_own_usage()
    {
        var a = await NewTenantAsync("R1", credits: 100);
        var b = await NewTenantAsync("R1B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        (await EnrollAsync(b.Token, "b-only", TestImages.Person(80))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await CsvAsync($"/api/v1/client/reports/usage.csv?from={Today}&to={Today}", a.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe($"usage-{Today.Replace("-", string.Empty)}-{Today.Replace("-", string.Empty)}.csv");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var lines = Lines(body);
        lines[0].ShouldBe("date,operation,outcome,requests,credits_charged");
        lines.Skip(1).Order(StringComparer.Ordinal).ToList().ShouldBe(
        [
            $"{Today},Enroll,Enrolled,2,2",
            $"{Today},Identify,Matched,1,2",
            $"{Today},Verify,Matched,1,1",
            $"{Today},Verify,NoMatch,1,1",
        ]);
        body.ShouldNotContain("b-only");
        body.ShouldNotContain("emp-1"); // person references never appear in an export
    }

    [Fact]
    public async Task The_admin_export_is_billed_usage_per_client_from_the_ledger()
    {
        var a = await NewTenantAsync("R2A", credits: 100);
        var b = await NewTenantAsync("R2B", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        (await EnrollAsync(b.Token, "b-only", TestImages.Person(80))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var (response, body) = await CsvAsync($"/api/v1/admin/reports/usage.csv?from={Today}&to={Today}", Platform.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lines = Lines(body);
        lines[0].ShouldBe("date,client_code,client_name,operation,billed_operations,credits_consumed,refunds,credits_refunded");
        lines.Skip(1).Order(StringComparer.Ordinal).ToList().ShouldBe(
        [
            $"{Today},R2A,Acme R2A,Enroll,2,2,0,0",
            $"{Today},R2A,Acme R2A,Identify,1,2,0,0",
            $"{Today},R2A,Acme R2A,Verify,2,2,0,0",
            $"{Today},R2B,Acme R2B,Enroll,1,1,0,0",
        ]);
        body.ShouldNotContain("b-only");
    }

    [Fact]
    public async Task Cells_that_start_like_formulas_are_neutralised_in_the_export()
    {
        var a = await NewTenantAsync("R3", credits: 100);
        await RunRecognitionsAsync(a, seed: 10);
        await App.WithDbAsync(async db =>
        {
            var client = await db.Clients.SingleAsync(c => c.Id == a.ClientId);
            client.Name = "=HYPERLINK(\"http://evil.test\",\"click\")";
            await db.SaveChangesAsync();
            return true;
        });

        var (_, body) = await CsvAsync($"/api/v1/admin/reports/usage.csv?from={Today}&to={Today}", Platform.AccessToken);

        body.ShouldContain(",\"'=HYPERLINK(\"\"http://evil.test\"\",\"\"click\"\")\",");
        body.ShouldNotContain(",=HYPERLINK");
        body.ShouldNotContain(",\"=HYPERLINK");
    }

    [Theory]
    [InlineData("from=2026-01-01&to=2026-04-03", HttpStatusCode.BadRequest)] // 93 days
    [InlineData("from=2026-01-01&to=2026-04-02", HttpStatusCode.OK)] // exactly 92 days
    [InlineData("from=2026-02-01&to=2026-01-01", HttpStatusCode.BadRequest)] // reversed
    [InlineData("from=2020-01-01", HttpStatusCode.BadRequest)] // open-ended: measured to today
    [InlineData("from=not-a-date&to=2026-01-01", HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.OK)] // default: the last 30 days
    public async Task The_range_is_bounded_for_both_exports(string query, HttpStatusCode expected)
    {
        var a = await NewTenantAsync("R4");

        var client = await App.GetAsync($"/api/v1/client/reports/usage.csv?{query}", a.Token);
        var admin = await App.GetAsync($"/api/v1/admin/reports/usage.csv?{query}", Platform.AccessToken);

        client.StatusCode.ShouldBe(expected);
        admin.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.BadRequest)
        {
            (await client.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task Exports_require_authentication_and_the_right_permission()
    {
        var a = await NewTenantAsync("R5");

        (await App.GetAsync("/api/v1/client/reports/usage.csv")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync("/api/v1/admin/reports/usage.csv")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync("/api/v1/admin/reports/usage.csv", a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.GetAsync("/api/v1/client/reports/usage.csv", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // staff are not a client
    }

    [Fact]
    public async Task Every_export_is_audit_logged_in_the_right_tenant()
    {
        var a = await NewTenantAsync("R6");
        (await App.GetAsync($"/api/v1/client/reports/usage.csv?from={Today}&to={Today}", a.Token)).EnsureSuccessStatusCode();
        (await App.GetAsync($"/api/v1/admin/reports/usage.csv?from={Today}&to={Today}", Platform.AccessToken)).EnsureSuccessStatusCode();

        var audit = await App.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(l => l.Action == "report.exported").OrderBy(l => l.OccurredAt).ToListAsync());

        audit.Count.ShouldBe(2);
        audit[0].ClientId.ShouldBe(a.ClientId);
        audit[0].EntityId.ShouldBe("client-usage");
        audit[0].NewValuesJson!.ShouldContain(Today);
        audit[1].EntityId.ShouldBe("platform-usage");
        audit[1].ClientId.ShouldNotBe(a.ClientId);
    }

    [Fact]
    public async Task Refunds_show_in_the_exports_and_totals_match_the_ledger()
    {
        var t = await NewTenantAsync("R7", credits: 100);
        await RunRecognitionsAsync(t, seed: 10);
        var identifyCharge = await App.WithTenantDbAsync(t.ClientId, db => db.LicenseTransactions.AsNoTracking()
            .Where(x => x.Type == LedgerEntryType.Consume && x.Operation == MeteredOperation.Identify).Select(x => x.Id).SingleAsync());
        (await App.PostAsync($"/api/v1/admin/transactions/{identifyCharge}/refund", new NexaVerify.Contracts.Licensing.RefundRequest("oops"), Platform.AccessToken)).EnsureSuccessStatusCode();

        var (_, admin) = await CsvAsync($"/api/v1/admin/reports/usage.csv?from={Today}&to={Today}", Platform.AccessToken);
        var (_, client) = await CsvAsync($"/api/v1/client/reports/usage.csv?from={Today}&to={Today}", t.Token);
        var (consumed, refunded, charged) = await LedgerTotalsAsync(t.ClientId);

        Lines(admin).Skip(1).Select(l => long.Parse(l.Split(',')[5])).Sum().ShouldBe(consumed);
        Lines(admin).Skip(1).Select(l => long.Parse(l.Split(',')[7])).Sum().ShouldBe(refunded);
        Lines(client).Skip(1).Select(l => long.Parse(l.Split(',')[4])).Sum().ShouldBe(charged);
        charged.ShouldBe(consumed);
        refunded.ShouldBe(2);
    }

    [Theory]
    [InlineData("from=2020-01-01&to=9999-12-31")]
    [InlineData("to=0001-01-01")]
    [InlineData("from=0001-01-01&to=0001-02-01")]
    public async Task Dates_outside_the_supported_range_are_rejected_before_anything_is_exported(string query)
    {
        var (response, _) = await CsvAsync($"/api/v1/admin/reports/usage.csv?{query}", Platform.AccessToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
    }
}
