using System.Net;
using System.Text.Json;
using NexaVerify.Contracts.Licensing;

namespace NexaVerify.Web.ComponentTests;

/// <summary>The portal's licensing client against the non-blocking ledger check and the two-person adjustment rule.</summary>
public class LedgerRunAndAdjustmentClientTests
{
    private static string RunJson(Guid id, string status, int licenses = 0, int broken = 0, string? error = null, string breaks = "[]") =>
        $$"""
        {"id":"{{id}}","status":"{{status}}","trigger":"manual","licenseId":null,"startedAt":"2026-06-15T09:00:00Z","finishedAt":{{(status == "Running" ? "null" : "\"2026-06-15T09:01:00Z\"")}},
         "licensesChecked":{{licenses}},"entriesChecked":{{licenses * 10}},"brokenLicenses":{{broken}},"breaks":{{breaks}},"error":{{(error is null ? "null" : "\"" + error + "\"")}}}
        """;

    private static async Task<(LicensingApiClient Client, BffHarness H)> BuildAsync()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        return (new LicensingApiClient(h.Gateway), h);
    }

    [Fact]
    public async Task The_ledger_check_starts_the_run_then_polls_it_until_it_finishes()
    {
        var (client, h) = await BuildAsync();
        var id = Guid.NewGuid();
        var polls = 0;
        h.Api.Respond = request => request.Method == HttpMethod.Post
            ? ScriptedApi.Json(HttpStatusCode.Accepted, RunJson(id, "Running"))
            : ScriptedApi.Json(HttpStatusCode.OK, ++polls < 3
                ? RunJson(id, "Running")
                : RunJson(id, "Completed", licenses: 4, broken: 1, breaks: $$"""[{"licenseId":"{{Guid.NewGuid()}}","clientId":"{{Guid.NewGuid()}}","firstBrokenEntryId":9,"reason":"Entry 9: content does not match its hash."}]"""));
        var previous = LicensingApiClient.LedgerPollInterval;
        LicensingApiClient.LedgerPollInterval = TimeSpan.Zero;
        try
        {
            var result = await client.VerifyAllLedgersAsync();

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            var report = result.Value;
            (report.LicensesChecked, report.EntriesChecked, report.BrokenLicenses).ShouldBe((4, 40L, 1));
            report.Breaks.ShouldHaveSingleItem().FirstBrokenEntryId.ShouldBe(9);
            h.Api.Seen[0].Method.ShouldBe(HttpMethod.Post);
            h.Api.Seen[0].Path.ShouldBe("/api/v1/admin/licensing/verify-ledger");
            h.Api.Seen.Skip(1).ShouldAllBe(s => s.Method == HttpMethod.Get && s.Path == $"/api/v1/admin/licensing/verify-ledger/runs/{id}");
            h.Api.Seen.Count.ShouldBe(1 + 3);
        }
        finally
        {
            LicensingApiClient.LedgerPollInterval = previous;
        }
    }

    [Fact]
    public async Task A_conflict_when_a_check_is_already_running_is_passed_on()
    {
        var (client, h) = await BuildAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Conflict, "{\"code\":\"CONFLICT\",\"detail\":\"A ledger verification is already running.\"}", "c-9");

        var result = await client.VerifyAllLedgersAsync();

        result.IsSuccess.ShouldBeFalse();
        (result.Error!.Status, result.Error.CorrelationId).ShouldBe((409, "c-9"));
        h.Api.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_that_failed_is_reported_as_an_error_not_as_a_clean_result()
    {
        var (client, h) = await BuildAsync();
        var id = Guid.NewGuid();
        h.Api.Respond = request => request.Method == HttpMethod.Post
            ? ScriptedApi.Json(HttpStatusCode.Accepted, RunJson(id, "Running"))
            : ScriptedApi.Json(HttpStatusCode.OK, RunJson(id, "Failed", error: "Interrupted: the process running it stopped before it finished."));
        var previous = LicensingApiClient.LedgerPollInterval;
        LicensingApiClient.LedgerPollInterval = TimeSpan.Zero;
        try
        {
            var result = await client.VerifyAllLedgersAsync();

            result.IsSuccess.ShouldBeFalse();
            result.Error!.Message.ShouldContain("Interrupted");
        }
        finally
        {
            LicensingApiClient.LedgerPollInterval = previous;
        }
    }

    [Fact]
    public async Task A_polling_error_stops_the_wait()
    {
        var (client, h) = await BuildAsync();
        var id = Guid.NewGuid();
        h.Api.Respond = request => request.Method == HttpMethod.Post
            ? ScriptedApi.Json(HttpStatusCode.Accepted, RunJson(id, "Running"))
            : ScriptedApi.Json(HttpStatusCode.Forbidden, "{\"code\":\"FORBIDDEN\",\"detail\":\"No.\"}");
        var previous = LicensingApiClient.LedgerPollInterval;
        LicensingApiClient.LedgerPollInterval = TimeSpan.Zero;
        try
        {
            (await client.VerifyAllLedgersAsync()).Error!.Status.ShouldBe(403);
        }
        finally
        {
            LicensingApiClient.LedgerPollInterval = previous;
        }
    }

    [Fact]
    public async Task An_adjustment_within_the_cap_comes_back_as_the_changed_license()
    {
        var (client, h) = await BuildAsync();
        var license = Sample.License();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(license, JsonOptions));

        var result = await client.AdjustAsync(license.Id, new AdjustLicenseRequest(50, "Correction"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Pending.ShouldBeNull();
        result.Value.License!.Id.ShouldBe(license.Id);
    }

    [Fact]
    public async Task An_adjustment_above_the_cap_comes_back_as_a_pending_request()
    {
        var (client, h) = await BuildAsync();
        var pending = new AdjustmentRequestDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50_000, "Goodwill", Guid.NewGuid(), Sample.Now, Sample.Now.AddHours(24), "Pending", null, null, null, null);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Accepted, JsonSerializer.Serialize(pending, JsonOptions));

        var result = await client.AdjustAsync(pending.LicenseId, new AdjustLicenseRequest(50_000, "Goodwill"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.License.ShouldBeNull();
        result.Value.Pending!.Id.ShouldBe(pending.Id);
        result.Value.Pending.Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task A_refused_adjustment_keeps_the_error()
    {
        var (client, h) = await BuildAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.BadRequest, "{\"code\":\"VALIDATION_FAILED\",\"detail\":\"Reason required.\",\"errors\":{\"reason\":[\"Required.\"]}}", "c-1");

        var result = await client.AdjustAsync(Guid.NewGuid(), new AdjustLicenseRequest(5, ""));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.FieldErrors!["reason"].ShouldBe(["Required."]);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public class LicenseAdjustmentPageTests : PageTestBase
{
    [Fact]
    public void An_adjustment_that_needs_a_second_approver_leaves_the_license_as_it_was()
    {
        SignInAsEverything();
        var providers = Providers();
        var before = Sample.License();
        Licensing.License = () => before;
        Licensing.AdjustOutcomeFor = request => new AdjustOutcome(null,
            new NexaVerify.Contracts.Licensing.AdjustmentRequestDto(Guid.NewGuid(), before.Id, before.ClientId, request.Credits, request.Reason, Guid.NewGuid(), Sample.Now, Sample.Now.AddHours(24), "Pending", null, null, null, null));
        var cut = Render<NexaVerify.Web.Pages.Admin.LicenseDetail>(p => p.Add(c => c.Id, before.Id));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adjust-license]").Count.ShouldBe(1));

        cut.Find("[data-testid=adjust-license]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        providers.FindComponent<MudNumericField<int>>().Find("input").Change("50000");
        providers.Find(".mud-dialog textarea").Input("Goodwill credit approved by finance");
        providers.Find("[data-testid=form-submit]").Click();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("adjust:50000:Goodwill credit approved by finance"));
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=form-submit]").ShouldBeEmpty("the dialog closes: the request was accepted"));
        cut.Markup.ShouldContain("842"); // the balance shown is still the old one
    }
}
