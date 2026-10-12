using System.Reflection;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class VerificationApiClientTests
{
    private static async Task<(BffHarness Harness, VerificationApiClient Client)> NewAsync()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        return (h, new VerificationApiClient(h.Gateway));
    }

    private const string AcceptedJson = "{\"requestId\":\"00000000-0000-0000-0000-00000000e001\",\"type\":\"Pan\",\"status\":\"Queued\",\"createdAt\":\"2026-06-15T09:00:00Z\",\"statusUrl\":\"/api/v1/verifications/x/status\",\"pollAfterSeconds\":2,\"replayed\":false}";

    [Fact]
    public async Task A_bank_statement_goes_as_multipart_with_the_key_and_without_the_original_file_name()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Accepted, AcceptedJson);
        var data = new byte[200_000];
        "%PDF-1.7"u8.CopyTo(data);
        var sent = new List<long>();

        var result = await client.SubmitBankStatementAsync(
            new SubmitBankStatementRequest("EXT-1", "CONSENT-9", "Priya Sharma", null, "4821", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)),
            new DocumentUploadFile(data, "application/pdf", "priya-sharma-statement.pdf"), "key-123", new Progress<long>(sent.Add));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.PollAfterSeconds.ShouldBe(2);
        var seen = h.Api.Seen.Single();
        seen.Method.ShouldBe(HttpMethod.Post);
        seen.Path.ShouldBe("/api/v1/verifications/bank-statements");
        seen.Headers["Idempotency-Key"].ShouldBe("key-123");
        seen.ContentType!.ShouldStartWith("multipart/form-data");
        var text = Encoding.Latin1.GetString(seen.BodyBytes!);
        text.ShouldContain("name=consentReference");
        text.ShouldContain("CONSENT-9");
        text.ShouldContain("name=periodFrom");
        text.ShouldContain("2026-01-01");
        text.ShouldContain("name=document; filename=upload.pdf");
        text.ShouldNotContain("priya-sharma-statement", Case.Insensitive);
        text.ShouldNotContain("name=bankName", Case.Sensitive);
        await Task.Delay(50);
        sent.Count.ShouldBeGreaterThan(1, "progress is reported in chunks");
        sent.Max().ShouldBe(data.Length);
    }

    [Fact]
    public async Task Pan_without_a_document_sends_no_file_part()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Accepted, AcceptedJson);

        var result = await client.SubmitPanAsync(new SubmitPanRequest(null, "C-1", "ABCDE1234F", "Priya", null, null), null, "k");

        result.IsSuccess.ShouldBeTrue();
        var seen = h.Api.Seen.Single();
        seen.Path.ShouldBe("/api/v1/verifications/pan");
        Encoding.Latin1.GetString(seen.BodyBytes!).ShouldNotContain("name=document");
    }

    [Fact]
    public async Task Passport_uses_its_plural_route()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Accepted, AcceptedJson);

        await client.SubmitPassportAsync(new SubmitPassportRequest(null, "C", null, null, null, null, null, null), new DocumentUploadFile([1, 2], "image/png", "a.png"), "k");

        h.Api.Seen.Single().Path.ShouldBe("/api/v1/verifications/passports");
    }

    [Fact]
    public async Task A_disabled_module_keeps_its_code_so_the_page_can_show_the_unavailable_state()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.NotFound, "{\"code\":\"VERIFICATION_MODULE_DISABLED\",\"detail\":\"off\"}", "corr-off");

        var result = await client.SubmitPanAsync(new SubmitPanRequest(null, "C", "ABCDE1234F", "P", null, null), null, "k");

        result.Error!.Code.ShouldBe("VERIFICATION_MODULE_DISABLED");
        result.Error.CorrelationId.ShouldBe("corr-off");
        result.Error.Status.ShouldBe(404);
    }

    [Fact]
    public async Task Status_polls_use_the_status_route()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"requestId\":\"00000000-0000-0000-0000-00000000e001\",\"type\":\"Pan\",\"status\":\"Processing\",\"stages\":{\"uploaded\":\"Completed\",\"extracted\":\"NotStarted\",\"rulesChecked\":\"NotStarted\",\"consistencyChecked\":\"NotStarted\",\"externalVerification\":\"NotRequested\",\"externalProviderId\":null},\"reviewRequired\":false,\"assurance\":\"None\",\"createdAt\":\"2026-06-15T09:00:00Z\",\"creditsCharged\":0,\"correlationId\":\"c\"}");

        var result = await client.GetStatusAsync(VerificationSample.Id, passive: true);

        result.Value.Status.ShouldBe("Processing");
        h.Api.Seen.Single().Path.ShouldBe($"/api/v1/verifications/{VerificationSample.Id}/status");
    }

    [Fact]
    public async Task The_list_sends_filters_and_the_transactions_page_size_is_capped()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"items\":[],\"page\":1,\"pageSize\":25,\"totalCount\":0}");

        await client.ListAsync(new VerificationListQuery { Type = "Pan", Status = "ReviewRequired", ReviewRequired = true, Page = 2 });
        await client.GetTransactionsAsync(VerificationSample.Id, new VerificationTransactionsQuery { Page = 3, PageSize = 5000 });

        var list = h.Api.Seen[0].Path;
        list.ShouldStartWith("/api/v1/verifications?");
        list.ShouldContain("type=Pan");
        list.ShouldContain("status=ReviewRequired");
        list.ShouldContain("reviewRequired=true");
        list.ShouldContain("page=2");
        h.Api.Seen[1].Path.ShouldContain("pageSize=500");
    }

    [Fact]
    public async Task Cancel_retry_and_erase_use_the_documented_verbs_and_routes()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        (await client.CancelAsync(VerificationSample.Id)).IsSuccess.ShouldBeTrue();
        (await client.RetryExternalAsync(VerificationSample.Id)).IsSuccess.ShouldBeTrue();
        (await client.EraseAsync(VerificationSample.Id)).IsSuccess.ShouldBeTrue();

        h.Api.Seen.Select(s => $"{s.Method} {s.Path}").ShouldBe(
        [
            $"POST /api/v1/verifications/{VerificationSample.Id}/cancel",
            $"POST /api/v1/verifications/{VerificationSample.Id}/retry-external",
            $"DELETE /api/v1/verifications/{VerificationSample.Id}/data",
        ]);
    }

    [Fact]
    public async Task The_catalogue_picks_up_credit_hints_when_the_api_adds_them()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "[{\"key\":\"pan\",\"displayName\":\"PAN\",\"description\":\"d\",\"status\":\"Available\",\"available\":true,\"requiredPermissions\":[],\"providerConfigured\":false,\"limits\":{\"maxFileBytes\":1000,\"extensions\":[\".pdf\"],\"maxPages\":2},\"plannedNote\":null,\"extractionCredits\":1,\"providerCredits\":2},{\"key\":\"aadhaar\",\"displayName\":\"Aadhaar\",\"description\":\"d\",\"status\":\"Upcoming\",\"available\":false,\"requiredPermissions\":[],\"providerConfigured\":null,\"limits\":null,\"plannedNote\":\"Planned\"}]");

        var modules = (await client.GetModulesAsync()).Value;

        modules[0].ExtractionCredits.ShouldBe(1);
        modules[0].ProviderCredits.ShouldBe(2);
        modules[1].IsUpcoming.ShouldBeTrue();
        modules[1].Limits.ShouldBeNull();
    }

    [Fact]
    public async Task Admin_calls_use_the_admin_routes_and_the_cost_reason_rides_along()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        var admin = new AdminVerificationApiClient(h.Gateway);
        h.Api.Respond = r => r.Method == HttpMethod.Put && r.Path.Contains("cost-rules")
            ? ScriptedApi.Json(HttpStatusCode.OK, "{\"id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":\"Platform\",\"clientId\":null,\"planId\":null,\"operation\":\"PanExtraction\",\"credits\":3,\"chargePolicy\":\"OnCompleted\",\"effectiveFrom\":\"2026-06-15T09:00:00Z\",\"effectiveTo\":null}")
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        await admin.UpdateModuleAsync("pan", new UpdateVerificationModuleRequest(false, "why", "RV"));
        await admin.SetDefaultCostAsync("PanExtraction", 3, "OnCompleted", "Pricing review");
        var client = Guid.NewGuid();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"items\":[],\"page\":1,\"pageSize\":25,\"totalCount\":0}");
        await admin.ListRequestsAsync(client, new AdminVerificationListQuery { Type = "Pan" });

        h.Api.Seen[0].Path.ShouldBe("/api/v1/admin/verifications/modules/pan");
        h.Api.Seen[0].Body!.ShouldContain("\"rowVersion\":\"RV\"");
        h.Api.Seen[1].Path.ShouldBe("/api/v1/admin/cost-rules/default");
        h.Api.Seen[1].Body!.ShouldContain("\"reason\":\"Pricing review\"");
        h.Api.Seen[2].Path.ShouldStartWith($"/api/v1/admin/clients/{client}/verifications?");
    }

    [Theory]
    [InlineData("/api/v1/verifications/downloads/AAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("https://api.example.test/api/v1/verifications/downloads/AAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("https://evil.example/other/path", false)]
    [InlineData("/api/v1/verifications/downloads/short", false)]
    [InlineData("/api/v1/verifications/downloads/AAAAAAAAAAAAAAAAAAAA/../../admin", false)]
    [InlineData("/api/v1/verifications/downloads/AAAAAAAAAAAAAAAAAAAA?next=http://evil", false)]
    [InlineData("//evil.example/api/v1/verifications/downloads/AAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void Only_the_apis_own_download_route_is_followed(string url, bool allowed)
    {
        VerificationDownloads.TryResolvePath(url, out var path).ShouldBe(allowed);
        if (allowed)
        {
            path.ShouldBe("verifications/downloads/AAAAAAAAAAAAAAAAAAAA", "the host in the link is ignored");
        }
    }
}

public class VerificationBffTests : IAsyncLifetime
{
    private readonly BffHarness _bff = new();
    private readonly FakeDownloads _downloads = new();
    private IHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        await _bff.SignInAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IApiGateway>(_bff.Gateway);
        builder.Services.AddSingleton<IVerificationDownloads>(_downloads);
        builder.Services.AddSingleton(new DownloadThrottle(_bff.Clock));
        builder.Services.Configure<SecurityHeadersOptions>(_ => { });
        builder.Services.AddAuthentication(HeaderAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
        builder.Services.AddPortalAuthorization();
        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPortalVerification();
        await app.StartAsync();
        _host = app;
        _http = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private static string Report(string format = "html") => $"/bff/client/verification/{VerificationSample.Id}/report?format={format}";

    private HttpRequestMessage Get(string path, string? user = "client:verification.report.download", string? fetchSite = "same-origin")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null) { request.Headers.Add("X-Test-User", user); }
        if (fetchSite is not null) { request.Headers.Add("Sec-Fetch-Site", fetchSite); }
        return request;
    }

    [Fact]
    public async Task A_report_is_relayed_as_a_locked_down_attachment_and_the_browser_never_sees_the_link()
    {
        var response = await _http.SendAsync(Get(Report()));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("<html>");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldBe($"nexaverify-check-{VerificationSample.Id:N}.html");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldBe(BillingEndpoints.InvoiceCsp);
        csp.ShouldStartWith("sandbox;");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("Cache-Control").Single().ShouldContain("no-store");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Single().ShouldBe("same-origin");
        response.Headers.Contains("Location").ShouldBeFalse();
        body.ShouldNotContain("/api/v1/verifications/downloads");
        _downloads.Calls[0].ShouldBe("link:Report:html:sid-1");
    }

    [Fact]
    public async Task Json_and_csv_downloads_use_their_own_kinds_and_types()
    {
        _downloads.File = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json") };
        var json = await _http.SendAsync(Get(Report("json")));
        json.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        json.Content.Headers.ContentDisposition!.FileName.ShouldEndWith(".json");

        _downloads.File = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a,b\n1,2\n", Encoding.UTF8, "text/csv") };
        var csv = await _http.SendAsync(Get($"/bff/client/verification/{VerificationSample.Id}/transactions.csv"));

        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentDisposition!.FileName.ShouldEndWith(".csv");
        _downloads.Calls.ShouldContain("link:TransactionsCsv::sid-1");
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task Other_websites_cannot_start_a_download(string fetchSite)
    {
        var response = await _http.SendAsync(Get(Report(), fetchSite: fetchSite));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _downloads.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_the_permission_or_from_the_wrong_portal_it_is_refused()
    {
        (await _http.SendAsync(Get(Report(), user: "client:verification.read"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _http.SendAsync(Get(Report(), user: "admin:verification.report.download"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _downloads.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_format_is_a_bad_request()
    {
        var response = await _http.SendAsync(Get(Report("pdf")));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _downloads.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Downloads_are_throttled_per_session_with_retry_after()
    {
        HttpResponseMessage? last = null;
        for (var i = 0; i < 20 && last?.StatusCode != HttpStatusCode.TooManyRequests; i++)
        {
            last = await _http.SendAsync(Get(Report()));
        }

        last!.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        last.Headers.GetValues("Retry-After").Single().ShouldNotBeNullOrEmpty();
        (await last.Content.ReadAsStringAsync()).ShouldContain("several downloads");
    }

    [Fact]
    public async Task A_refusal_from_the_api_keeps_its_status_and_reference_but_no_internals()
    {
        _downloads.Link = () => ApiResult<DownloadLinkDto>.Fail("FORBIDDEN", "You don't have permission to do this.", "corr-dl", 403);

        var response = await _http.SendAsync(Get(Report()));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        body.ShouldContain("corr-dl");
        _downloads.Calls.ShouldAllBe(c => c.StartsWith("link:"), "the file is never opened");
    }

    [Fact]
    public async Task A_file_of_the_wrong_type_is_not_passed_on()
    {
        _downloads.File = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/plain") };

        var response = await _http.SendAsync(Get(Report()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task An_oversized_file_is_not_passed_on()
    {
        _downloads.File = () =>
        {
            var content = new ByteArrayContent(new byte[VerificationEndpoints.MaxDownloadBytes + 10]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

        var response = await _http.SendAsync(Get(Report()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task The_real_downloader_requests_a_link_then_fetches_only_the_apis_own_route()
    {
        var real = new VerificationDownloads(_bff.Gateway);
        _bff.Api.Respond = r => r.Path.EndsWith("/downloads", StringComparison.Ordinal)
            ? ScriptedApi.Json(HttpStatusCode.OK, "{\"downloadUrl\":\"https://other-host.example/api/v1/verifications/downloads/BBBBBBBBBBBBBBBBBBBB\",\"expiresAt\":\"2026-06-15T09:01:00Z\"}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x", Encoding.UTF8, "text/csv") };
        var options = new ApiCallOptions { SessionId = "sid-1" };

        var link = await real.CreateLinkAsync(VerificationSample.Id, VerificationDownloadKind.TransactionsCsv, options, default);
        var opened = await real.OpenAsync(link.Value.DownloadUrl, VerificationDownloadKind.TransactionsCsv, options, default);

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        _bff.Api.Seen[0].Body!.ShouldContain("\"kind\":\"TransactionsCsv\"");
        _bff.Api.Seen[1].Path.ShouldBe("/api/v1/verifications/downloads/BBBBBBBBBBBBBBBBBBBB");
        _bff.Api.Seen[1].Headers["Accept"].ShouldContain("text/csv");

        var bad = await real.OpenAsync("https://evil.example/steal", VerificationDownloadKind.TransactionsCsv, options, default);
        bad.IsSuccess.ShouldBeFalse();
        _bff.Api.Seen.Count.ShouldBe(2, "a poisoned link makes no call at all");
    }

    [Fact]
    public async Task The_staff_statistics_export_uses_the_shared_csv_relay_and_needs_staff_permission()
    {
        _bff.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a,b\n", Encoding.UTF8, "text/csv") };

        var ok = await _http.SendAsync(Get("/bff/admin/verification/stats.csv?from=2026-06-01&to=2026-06-15", user: "admin:verification.admin.read"));
        var denied = await _http.SendAsync(Get("/bff/admin/verification/stats.csv", user: "client:verification.admin.read"));

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        _bff.Api.Seen.Single().Path.ShouldStartWith("/api/v1/admin/verifications/stats/export.csv");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

public class VerificationServicesTests
{
    [Fact]
    public void The_nine_permission_keys_are_mirrored_exactly()
    {
        var mirrored = new[]
        {
            WebPermissions.VerificationBankStatementSubmit, WebPermissions.VerificationPanSubmit, WebPermissions.VerificationPassportSubmit, WebPermissions.VerificationRead,
            WebPermissions.VerificationReportDownload, WebPermissions.VerificationDocumentDownload, WebPermissions.VerificationErase,
            WebPermissions.VerificationAdminRead, WebPermissions.VerificationAdminConfigure,
        };

        mirrored.ShouldBe(
        [
            "verification.bank-statement.submit", "verification.pan.submit", "verification.passport.submit", "verification.read", "verification.report.download",
            "verification.document.download", "verification.erase", "verification.admin.read", "verification.admin.configure",
        ]);
        mirrored.ShouldAllBe(k => Permissions.All.Any(p => p.Key == k));
    }

    [Fact]
    public void The_client_group_is_trimmed_by_permission_and_has_no_secondary_actions_in_the_sidebar()
    {
        var all = NavigationCatalog.Trim(NavigationCatalog.Client, _ => true).Single(g => g.Title == "Document verification");
        all.Items.Select(i => i.Href).ShouldBe(["client/verification", "client/verification/bank-statement", "client/verification/pan", "client/verification/passport", "client/verification/history"]);

        var pan = NavigationCatalog.Trim(NavigationCatalog.Client, p => p is WebPermissions.VerificationPanSubmit).Single(g => g.Title == "Document verification");
        pan.Items.Select(i => i.Href).ShouldBe(["client/verification/pan"]);

        var reader = NavigationCatalog.Trim(NavigationCatalog.Client, p => p == WebPermissions.VerificationRead).Single(g => g.Title == "Document verification");
        reader.Items.Select(i => i.Href).ShouldBe(["client/verification", "client/verification/history"]);

        NavigationCatalog.Trim(NavigationCatalog.Client, _ => false).ShouldNotContain(g => g.Title == "Document verification");
        var text = string.Join(' ', all.Items.Select(i => i.Title));
        text.ShouldNotContain("Download");
        text.ShouldNotContain("Details");
        all.Items.ShouldNotContain(i => i.Href.Contains("aadhaar") || i.Href.Contains("upcoming"));
    }

    [Fact]
    public void The_admin_group_is_trimmed_by_staff_permissions()
    {
        NavigationCatalog.Trim(NavigationCatalog.Admin, p => p == WebPermissions.VerificationAdminRead).Single(g => g.Title == "Verification").Items.Select(i => i.Href)
            .ShouldBe(["admin/verification", "admin/verification/modules", "admin/verification/requests"]);
        NavigationCatalog.Trim(NavigationCatalog.Admin, p => p == WebPermissions.AuditRead).Single(g => g.Title == "Verification").Items.Select(i => i.Href)
            .ShouldBe(["admin/verification/audit"]);
        NavigationCatalog.Trim(NavigationCatalog.Admin, p => p == WebPermissions.ClientsRead).ShouldNotContain(g => g.Title == "Verification");
    }

    [Fact]
    public void Every_verification_menu_entry_points_at_a_real_page()
    {
        var routes = typeof(NexaVerify.Web.Routes).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>())
            .Select(r => r.Template.Trim('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = NavigationCatalog.Client.Concat(NavigationCatalog.Admin).Where(g => g.Title is "Document verification" or "Verification").SelectMany(g => g.Items).ToList();

        items.Count.ShouldBe(9);
        foreach (var item in items)
        {
            item.Implemented.ShouldBeTrue();
            routes.ShouldContain(item.Href.Trim('/'), item.Title);
        }
    }

    [Fact]
    public void Pages_are_protected_by_the_permission_that_fits_them()
    {
        static string Policy(Type page) => page.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(a => a.Policy!).Single(p => p.StartsWith(Policies.PermissionPrefix, StringComparison.Ordinal));
        Policy(typeof(NexaVerify.Web.Pages.Client.Verification.PanVerification)).ShouldBe(Policies.PermissionPrefix + WebPermissions.VerificationPanSubmit);
        Policy(typeof(NexaVerify.Web.Pages.Client.Verification.VerificationHistory)).ShouldBe(Policies.PermissionPrefix + WebPermissions.VerificationRead);
        Policy(typeof(NexaVerify.Web.Pages.Admin.Verification.VerificationModules)).ShouldBe(Policies.PermissionPrefix + WebPermissions.VerificationAdminRead);
        Policy(typeof(NexaVerify.Web.Pages.Admin.Verification.VerificationAudit)).ShouldBe(Policies.PermissionPrefix + WebPermissions.AuditRead);
    }

    [Fact]
    public void Nothing_the_user_sees_calls_a_document_authentic_genuine_valid_or_active()
    {
        var forbidden = new System.Text.RegularExpressions.Regex(@"\b(authentic\w*|genuine\w*|valid|validated|active)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var strings = new List<string> { VerificationText.Disclaimer, VerificationText.SubmitPurposeNote, VerificationText.UpcomingNote };
        foreach (var status in VerificationStatuses.All) { strings.Add(VerificationText.StatusLabel(status)); strings.Add(VerificationText.Headline(status, "NotRequested", null, "ChecksCompleted")); }
        foreach (var state in ExternalStates.All)
        {
            strings.Add(VerificationText.ExternalLabel(state));
            strings.Add(VerificationText.ExternalHeadline(state, "acme"));
            foreach (var type in VerificationTypes.All) { strings.Add(VerificationText.ExternalExplanation(state, type)); }
        }

        foreach (var outcome in new[] { "Pass", "Warn", "Fail", "NotApplicable", "NotEvaluated" }) { strings.Add(VerificationText.CheckOutcomeLabel(outcome)); }
        foreach (var outcome in new[] { "Match", "PartialMatch", "Mismatch", "NotProvided", "NotExtracted" }) { strings.Add(VerificationText.MatchLabel(outcome)); }
        foreach (var mrz in new[] { "Valid", "Invalid", "NotPresent" }) { strings.Add(VerificationText.MrzLabel(mrz)); }
        foreach (var code in new[] { "DOCUMENT_UNREADABLE", "OCR_UNAVAILABLE", "PROCESSING_FAILED", "PROCESSING_TIMEOUT", "CREDITS_UNAVAILABLE", "CANCELLED", "EXPIRED" }) { strings.Add(VerificationText.FailureMessage(code, null)); }
        foreach (var code in new[] { "DOCUMENT_REQUIRED", "DOCUMENT_INVALID", "DOCUMENT_UNSUPPORTED_TYPE", "DOCUMENT_TOO_LARGE", "DOCUMENT_TOO_MANY_PAGES", "DOCUMENT_ENCRYPTED", "DOCUMENT_ACTIVE_CONTENT", "VERIFICATION_MODULE_DISABLED", "VERIFICATION_QUEUE_FULL", "PROCESSING_UNAVAILABLE" })
        {
            strings.Add(VerificationText.SubmitProblem(new ApiError(code, "x")));
        }

        foreach (var s in strings)
        {
            forbidden.IsMatch(s).ShouldBeFalse(s);
        }
    }

    [Fact]
    public void Only_verified_has_a_positive_headline_and_it_needs_the_authoritative_assurance()
    {
        VerificationText.Headline("Completed", "Verified", "acme", "ChecksCompleted").ShouldBe("Checks completed. Not officially verified.", "the provider's word alone, without the server's assurance, is not enough");
        VerificationText.Headline("Completed", "Verified", "acme", "AuthoritativelyVerified").ShouldBe("Officially verified by acme");
        foreach (var state in ExternalStates.All.Where(s => s != "Verified"))
        {
            VerificationText.ExternalHeadline(state, "acme").ShouldNotContain("verified", Case.Insensitive, state);
        }
    }

    [Fact]
    public void The_sniffer_recognises_by_content_rejects_executables_and_checks_names()
    {
        DocumentSniffer.Detect(Encoding.Latin1.GetBytes("%PDF-1.7")).ShouldBe("application/pdf");
        DocumentSniffer.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]).ShouldBe("image/png");
        DocumentSniffer.Detect([0x4D, 0x5A, 0x90, 0x00]).ShouldBeNull();
        DocumentSniffer.ExtensionMatches("A.JPEG", "image/jpeg").ShouldBeTrue();
        DocumentSniffer.ExtensionMatches("a.pdf", "image/png").ShouldBeFalse();
        DocumentSniffer.EstimatePdfPages(Encoding.ASCII.GetBytes("<< /Type /Pages >> << /Type/Page >> << /Type /Page >>")).ShouldBe(2);
        DocumentSniffer.EstimatePdfPages([1, 2, 3]).ShouldBeNull();
    }

    [Fact]
    public void Retry_and_cancel_rules_follow_state_and_permission()
    {
        Func<string, bool> all = _ => true;
        VerificationActions.CanCancel("Queued", "Pan", all).ShouldBeTrue();
        VerificationActions.CanCancel("Completed", "Pan", all).ShouldBeFalse();
        VerificationActions.CanCancel("Queued", "Pan", p => p != WebPermissions.VerificationPanSubmit).ShouldBeFalse();
        VerificationActions.CanRetryExternal("Completed", "Timeout", "Pan", null, all).ShouldBeTrue();
        VerificationActions.CanRetryExternal("Completed", "ProviderNotConfigured", "Pan", null, all).ShouldBeFalse();
        VerificationActions.CanRetryExternal("Completed", "ProviderNotConfigured", "Pan", true, all).ShouldBeTrue();
        VerificationActions.CanRetryExternal("Completed", "Verified", "Pan", true, all).ShouldBeFalse();
        VerificationActions.CanRetryExternal("Failed", "Timeout", "Pan", true, all).ShouldBeFalse();
        VerificationActions.CanRetryExternal("Completed", "Timeout", "Pan", true, all, dataPurged: true).ShouldBeFalse();
    }

    [Fact]
    public void Amounts_use_the_shared_money_helper()
    {
        VerificationText.Amount(1234.5m, "INR").ShouldBe("₹1,234.50");
        VerificationText.Amount(null, "INR").ShouldBe("—");
    }

    [Fact]
    public void Form_rules_mirror_the_api_inline()
    {
        static IList<System.ComponentModel.DataAnnotations.ValidationResult> Check(object model)
        {
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new System.ComponentModel.DataAnnotations.ValidationContext(model), results, true);
            return results;
        }

        Check(new BankStatementForm { ConsentReference = "c", AccountNumberLast4 = "12" }).ShouldContain(r => r.ErrorMessage!.Contains("last 4 digits"));
        Check(new BankStatementForm { ConsentReference = "c", PeriodFrom = new DateTime(2026, 5, 1), PeriodTo = new DateTime(2026, 4, 1) }).ShouldContain(r => r.ErrorMessage!.Contains("start before"));
        Check(new BankStatementForm()).ShouldContain(r => r.ErrorMessage!.Contains("consent"));
        Check(new PanForm { ConsentReference = "c", FullName = "P", PanNumber = " abcde1234f " }).ShouldBeEmpty();
        Check(new PassportForm { ConsentReference = "c", Nationality = "IN" }).ShouldContain(r => r.ErrorMessage!.Contains("3-letter"));
        Check(new PassportForm { ConsentReference = "c", DateOfIssue = new DateTime(2030, 1, 1), DateOfExpiry = new DateTime(2029, 1, 1) }).ShouldContain(r => r.ErrorMessage!.Contains("before the expiry"));
        new PanForm { PanNumber = " abcde1234f ", ConsentReference = " c " }.ToRequest().PanNumber.ShouldBe("ABCDE1234F");
    }
}

public class VerificationPollerTests
{
    [Fact]
    public void The_delays_back_off_and_honour_the_apis_hint_for_the_first_wait()
    {
        VerificationPoller.DelayFor(0).ShouldBe(TimeSpan.FromSeconds(2));
        VerificationPoller.DelayFor(0, 7).ShouldBe(TimeSpan.FromSeconds(7));
        VerificationPoller.DelayFor(0, 9999).ShouldBe(TimeSpan.FromSeconds(30));
        VerificationPoller.DelayFor(5).ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task It_stops_at_a_terminal_status_and_gives_up_after_the_time_limit()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var poller = new VerificationPoller(clock);
        var calls = 0;
        var done = poller.RunAsync(_ => { calls++; return Ok.Of(VerificationSample.Status(calls < 3 ? "Processing" : "Completed")); }, _ => Task.CompletedTask, default);
        while (!done.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(3)); await Task.Delay(1); }
        (await done).TimedOut.ShouldBeFalse();
        calls.ShouldBe(3);

        var forever = poller.RunAsync(_ => Ok.Of(VerificationSample.Status("Processing")), _ => Task.CompletedTask, default);
        while (!forever.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(5)); await Task.Delay(1); }
        (await forever).TimedOut.ShouldBeTrue();
    }

    [Fact]
    public async Task A_definite_error_stops_it_but_a_hiccup_does_not()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var poller = new VerificationPoller(clock);
        var notFound = await poller.RunAsync(_ => Ok.Fail<VerificationStatusDto>("NOT_FOUND", "x", "c", 404), _ => Task.CompletedTask, default);
        notFound.Last.IsSuccess.ShouldBeFalse();

        var calls = 0;
        var hiccup = poller.RunAsync(_ => ++calls == 1 ? Ok.Fail<VerificationStatusDto>("API_UNAVAILABLE", "x", null, 503) : Ok.Of(VerificationSample.Status()), _ => Task.CompletedTask, default);
        while (!hiccup.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(3)); await Task.Delay(1); }
        (await hiccup).Last.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancelling_ends_it_cleanly()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var run = new VerificationPoller(clock).RunAsync(_ => Ok.Of(VerificationSample.Status("Processing")), _ => Task.CompletedTask, cts.Token);
        await Task.Delay(20);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(run);
    }

    [Fact]
    public void One_idempotency_key_per_logical_submit()
    {
        var attempt = new VerificationAttempt();
        var doc = new DocumentUploadFile([1, 2, 3], "application/pdf", "a.pdf");
        var request = new SubmitPanRequest(null, "c", "ABCDE1234F", "P", null, null);

        var first = attempt.KeyFor(request, doc);

        attempt.KeyFor(request, doc).ShouldBe(first);
        attempt.KeyFor(request with { FullName = "Q" }, doc).ShouldNotBe(first);
        attempt.KeyFor(request, new DocumentUploadFile([9], "application/pdf", "a.pdf")).ShouldNotBe(first);
        attempt.Reset();
        attempt.KeyFor(request, doc).ShouldNotBe(first);
    }
}
