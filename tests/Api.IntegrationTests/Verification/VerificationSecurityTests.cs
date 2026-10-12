using System.Net;
using System.Net.Http.Json;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.TestSupport;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Api.IntegrationTests.Verification;

/// <summary>Authentication, authorisation, tenant isolation, request validation and upload hardening of the verification API.</summary>
[Collection(SqlServerCollection.Name)]
public class VerificationSecurityTests : VerificationTestBase
{
    public VerificationSecurityTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    private static readonly (HttpMethod Method, string Path)[] ClientEndpoints =
    [
        (HttpMethod.Get, "/api/v1/verifications/modules"),
        (HttpMethod.Get, "/api/v1/verifications"),
        (HttpMethod.Get, "/api/v1/verifications/00000000-0000-0000-0000-000000000001"),
        (HttpMethod.Get, "/api/v1/verifications/00000000-0000-0000-0000-000000000001/status"),
        (HttpMethod.Get, "/api/v1/verifications/00000000-0000-0000-0000-000000000001/transactions"),
        (HttpMethod.Post, "/api/v1/verifications/00000000-0000-0000-0000-000000000001/cancel"),
        (HttpMethod.Post, "/api/v1/verifications/00000000-0000-0000-0000-000000000001/retry-external"),
        (HttpMethod.Delete, "/api/v1/verifications/00000000-0000-0000-0000-000000000001/data"),
        (HttpMethod.Post, "/api/v1/verifications/00000000-0000-0000-0000-000000000001/downloads"),
        (HttpMethod.Get, "/api/v1/verifications/downloads/sometoken"),
        (HttpMethod.Post, "/api/v1/verifications/bank-statements"),
        (HttpMethod.Post, "/api/v1/verifications/pan"),
        (HttpMethod.Post, "/api/v1/verifications/passports"),
    ];

    private static readonly (HttpMethod Method, string Path)[] AdminEndpoints =
    [
        (HttpMethod.Get, "/api/v1/admin/verifications/modules"),
        (HttpMethod.Put, "/api/v1/admin/verifications/modules/pan"),
        (HttpMethod.Get, "/api/v1/admin/verifications/stats"),
        (HttpMethod.Get, "/api/v1/admin/verifications/stats/export.csv"),
        (HttpMethod.Get, "/api/v1/admin/verifications/audit"),
        (HttpMethod.Get, "/api/v1/admin/clients/00000000-0000-0000-0000-000000000001/verifications"),
        (HttpMethod.Get, "/api/v1/admin/clients/00000000-0000-0000-0000-000000000001/verifications/00000000-0000-0000-0000-000000000002"),
    ];

    private Task<HttpResponseMessage> CallAsync((HttpMethod Method, string Path) endpoint, string? token) =>
        App.SendAsync(new HttpRequestMessage(endpoint.Method, endpoint.Path), token);

    [Fact]
    public async Task Every_endpoint_requires_authentication()
    {
        foreach (var endpoint in ClientEndpoints.Concat(AdminEndpoints))
        {
            (await CallAsync(endpoint, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, endpoint.Path);
        }
    }

    [Fact]
    public async Task Platform_staff_cannot_use_the_client_endpoints_and_clients_cannot_use_the_admin_endpoints()
    {
        var t = await NewTenantAsync("S1");
        foreach (var endpoint in ClientEndpoints)
        {
            (await CallAsync(endpoint, Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, endpoint.Path);
        }

        foreach (var endpoint in AdminEndpoints)
        {
            (await CallAsync(endpoint, t.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, endpoint.Path);
        }
    }

    [Fact]
    public async Task A_client_user_without_the_verification_permissions_is_refused()
    {
        var t = await NewTenantAsync("S2");
        await App.CreateClientUserAsync(t.ClientId, "plain@s2.test", "ClientUser");
        var user = await App.LoginAsync("plain@s2.test", AuthApp.StrongPassword);

        foreach (var endpoint in ClientEndpoints)
        {
            (await CallAsync(endpoint, user.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, endpoint.Path);
        }
    }

    [Fact]
    public async Task An_api_key_can_only_do_what_its_scopes_allow()
    {
        var t = await NewTenantAsync("S3");
        var created = await App.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("pan only", ["verification.pan.submit", "verification.read"], null, null, null), t.Token);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var key = (await created.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!.RawKey;

        HttpRequestMessage Request(string path, byte[] pdf, Dictionary<string, string> fields)
        {
            var form = new MultipartFormDataContent();
            foreach (var (k, v) in fields)
            {
                form.Add(new StringContent(v), k);
            }

            var part = new ByteArrayContent(pdf);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            form.Add(part, "document", "d.pdf");
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
            request.Headers.Add("X-Api-Key", key);
            return request;
        }

        var allowed = await App.Client.SendAsync(Request("/api/v1/verifications/pan", PanPdf(), PanFields()));
        allowed.StatusCode.ShouldBe(HttpStatusCode.Accepted, await allowed.Content.ReadAsStringAsync());
        var denied = await App.Client.SendAsync(Request("/api/v1/verifications/bank-statements", BankPdf(), BankFields()));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var id = (await ReadAsync<VerificationAcceptedDto>(allowed)).RequestId;
        var read = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/verifications/{id}/status");
        read.Headers.Add("X-Api-Key", key);
        (await App.Client.SendAsync(read)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Erasing needs verification.erase, which this key does not hold.
        var erase = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/verifications/{id}/data");
        erase.Headers.Add("X-Api-Key", key);
        (await App.Client.SendAsync(erase)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Another_clients_requests_look_like_they_do_not_exist()
    {
        var a = await NewTenantAsync("S4A");
        var b = await NewTenantAsync("S4B");
        var accepted = await AcceptedAsync(await SubmitBankAsync(a));
        await RunWorkerAsync();
        var link = await ReadAsync<DownloadLinkDto>(await App.PostAsync($"/api/v1/verifications/{accepted.RequestId}/downloads", new VerificationDownloadRequest("Report", "json"), a.Token));

        var id = accepted.RequestId;
        (await App.GetAsync($"/api/v1/verifications/{id}", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync($"/api/v1/verifications/{id}/status", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync($"/api/v1/verifications/{id}/transactions", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PostAsync($"/api/v1/verifications/{id}/cancel", null, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PostAsync($"/api/v1/verifications/{id}/retry-external", null, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.DeleteAsync($"/api/v1/verifications/{id}/data", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PostAsync($"/api/v1/verifications/{id}/downloads", new VerificationDownloadRequest("Report", "json"), b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync(link.DownloadUrl, b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // a token is useless to another tenant

        var list = await ReadAsync<PagedResult<VerificationListItemDto>>(await App.GetAsync("/api/v1/verifications", b.Token));
        list.Items.ShouldBeEmpty();

        // The owner is unaffected: B's failed attempt did not consume or alter A's link.
        (await App.GetAsync(link.DownloadUrl, a.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invalid_submissions_are_rejected_with_field_errors_and_stable_codes()
    {
        var t = await NewTenantAsync("S5");

        var noConsent = BankFields();
        noConsent.Remove("consentReference");
        var r1 = await SubmitBankAsync(t, fields: noConsent);
        r1.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var r1Body = await JsonOf(r1);
        r1Body.GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
        r1Body.GetProperty("errors").TryGetProperty("consentReference", out _).ShouldBeTrue();

        var badLast4 = BankFields();
        badLast4["accountNumberLast4"] = "12";
        (await SubmitBankAsync(t, fields: badLast4)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var inverted = BankFields();
        inverted["periodFrom"] = "2024-05-01";
        inverted["periodTo"] = "2024-04-01";
        (await SubmitBankAsync(t, fields: inverted)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var noFile = await PostDocumentAsync("/api/v1/verifications/bank-statements", t.Token, null, BankFields());
        noFile.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(noFile)).ShouldBe(ErrorCodes.DocumentRequired);

        var passportNoFile = await PostDocumentAsync("/api/v1/verifications/passports", t.Token, null, PassportFields());
        (await CodeOf(passportNoFile)).ShouldBe(ErrorCodes.DocumentRequired);

        var badNationality = PassportFields();
        badNationality["nationality"] = "INDIA";
        (await SubmitPassportAsync(t, fields: badNationality)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var neither = PanFields();
        neither.Remove("panNumber");
        var panNothing = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, null, neither);
        panNothing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await JsonOf(panNothing)).GetProperty("errors").TryGetProperty("document", out _).ShouldBeTrue();

        var noName = PanFields();
        noName.Remove("fullName");
        (await SubmitPanAsync(t, PanPdf(), noName)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var longKey = await SubmitBankAsync(t, key: new string('k', 101));
        longKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Nothing was stored or charged by any of the above.
        (await InTenantAsync(t, db => Task.FromResult(db.VerificationRequests.Count()))).ShouldBe(0);
        (await ConsumedAsync(t)).ShouldBe(0);
    }

    [Fact]
    public async Task A_pan_number_alone_is_accepted_as_number_only_mode()
    {
        var t = await NewTenantAsync("S6");
        var response = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, null, PanFields("ABCPV1234V"));
        var accepted = await AcceptedAsync(response);
        await RunWorkerAsync();

        var result = await ResultAsync(t, accepted.RequestId);
        result.Stages.Uploaded.ShouldBe("Skipped");
        result.Stages.Extracted.ShouldBe("Skipped");
        result.Stages.RulesChecked.ShouldBe("Completed");
        result.External.State.ShouldBe("Verified");
        result.Document.ShouldBeNull();
    }

    [Fact]
    public async Task Hostile_or_disguised_files_are_refused_before_anything_is_stored_or_charged()
    {
        var t = await NewTenantAsync("S7");
        var png = SampleDocuments.Png(40, 40);

        // Content does not agree with the name / declared type.
        var polyglot = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, png, PanFields(), fileName: "card.pdf", contentType: "application/pdf");
        polyglot.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(polyglot)).ShouldBeOneOf(ErrorCodes.DocumentInvalid, ErrorCodes.DocumentUnsupportedType);

        var html = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, "<html><script>alert(1)</script></html>"u8.ToArray(), PanFields(), fileName: "card.pdf");
        html.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(html)).ShouldBe(ErrorCodes.DocumentUnsupportedType);

        var exe = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, png, PanFields(), fileName: "card.exe", contentType: "image/png");
        (await CodeOf(exe)).ShouldBe(ErrorCodes.DocumentUnsupportedType);

        var traversal = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, png, PanFields(), fileName: "../../etc/passwd.png", contentType: "image/png");
        traversal.StatusCode.ShouldBeOneOf(HttpStatusCode.Accepted, HttpStatusCode.BadRequest); // the name is never used as a path; at worst the extension check refuses it

        var truncated = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, SampleDocuments.Pdf(["x"])[..40], PanFields());
        truncated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var active = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, SampleDocuments.RawPdf(catalogExtra: "/OpenAction << /S /JavaScript /JS (app.alert(1)) >>"), PanFields());
        active.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(active)).ShouldBe(ErrorCodes.DocumentActiveContent);

        var manyPages = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, SampleDocuments.BlankPdf(5), PanFields());
        manyPages.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(manyPages)).ShouldBe(ErrorCodes.DocumentTooManyPages);

        var bomb = await PostDocumentAsync("/api/v1/verifications/pan", t.Token, SampleDocuments.PngBomb(30000, 30000), PanFields(), fileName: "card.png", contentType: "image/png");
        bomb.StatusCode.ShouldBeOneOf(HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest);

        (await InTenantAsync(t, db => Task.FromResult(db.VerificationRequests.Count() + db.VerificationDocuments.Count() + db.VerificationDocumentBlobs.Count()))).ShouldBe(traversalStored(traversal));
        (await ConsumedAsync(t)).ShouldBe(0);
    }

    private static int traversalStored(HttpResponseMessage traversal) => traversal.StatusCode == HttpStatusCode.Accepted ? 3 : 0;

    [Fact]
    public async Task Upcoming_modules_are_listed_but_have_no_route_permission_or_price()
    {
        var t = await NewTenantAsync("S8");
        var modules = await ReadAsync<List<VerificationModuleDto>>(await App.GetAsync("/api/v1/verifications/modules", t.Token));

        modules.Where(m => m.Available).Select(m => m.Key).Order().ShouldBe(["bank-statement", "pan", "passport"]);
        var upcoming = modules.Where(m => m.Status == "Upcoming").ToList();
        upcoming.Count.ShouldBe(6);
        upcoming.ShouldAllBe(m => !m.Available && m.RequiredPermissions.Count == 0 && m.Limits == null && m.ProviderConfigured == null);

        foreach (var key in new[] { "aadhaar", "driving-licence", "voter-id", "digilocker", "govt-documents", "kyc-fraud" })
        {
            (await PostDocumentAsync($"/api/v1/verifications/{key}", t.Token, PanPdf(), PanFields())).StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        }

        (await ConsumedAsync(t)).ShouldBe(0);
    }
}
