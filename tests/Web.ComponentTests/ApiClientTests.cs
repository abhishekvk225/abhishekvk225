using System.Net;
using System.Text.Json;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;

namespace NexaVerify.Web.ComponentTests;

public class ApiClientTests
{
    private sealed record Payload(int Value);

    [Fact]
    public async Task Validation_problems_become_field_errors_with_the_correlation_id()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.BadRequest,
            "{\"status\":400,\"detail\":\"One or more validation errors occurred.\",\"code\":\"VALIDATION_FAILED\",\"correlationId\":\"corr-42\",\"errors\":{\"name\":[\"Name is required.\"],\"contactEmail\":[\"Email is not valid.\"]}}");

        var result = await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "admin/clients", new { });

        var error = result.Error!;
        error.Code.ShouldBe("VALIDATION_FAILED");
        error.Status.ShouldBe(400);
        error.CorrelationId.ShouldBe("corr-42");
        error.FieldErrors!["name"].ShouldBe(["Name is required."]);
        error.FieldErrors["CONTACTEMAIL"].ShouldBe(["Email is not valid."], "field lookup is case-insensitive so it maps onto form properties");
    }

    [Fact]
    public async Task Server_errors_show_a_generic_message_and_never_the_internal_detail()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.InternalServerError,
            "{\"detail\":\"System.NullReferenceException at NexaVerify.Application.Secret.Method in C:\\\\src\\\\X.cs:line 12\",\"code\":\"INTERNAL_ERROR\",\"correlationId\":\"corr-1\"}");

        var error = (await h.Gateway.GetAsync<Payload>("x")).Error!;

        error.Message.ShouldNotContain("NullReference");
        error.Message.ShouldNotContain("C:\\");
        error.Message.ShouldBe("Something went wrong on our side. Please try again.");
        error.CorrelationId.ShouldBe("corr-1");
    }

    [Theory]
    [InlineData(403, "FORBIDDEN", "You don't have permission to do this.")]
    [InlineData(404, "NOT_FOUND", "We couldn't find that. It may have been removed.")]
    [InlineData(429, "RATE_LIMITED", "Too many requests right now. Please wait a moment and try again.")]
    [InlineData(409, "CONCURRENCY_CONFLICT", "Someone else changed this at the same time. Reload and try again.")]
    public async Task Common_failures_get_plain_language_messages(int status, string code, string message)
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json((HttpStatusCode)status, $"{{\"code\":\"{code}\",\"detail\":\"internal wording\"}}");

        var error = (await h.Gateway.GetAsync<Payload>("x")).Error!;

        error.Message.ShouldBe(message);
        error.Code.ShouldBe(code);
    }

    [Fact]
    public async Task Business_rule_messages_from_the_api_are_passed_on_when_short()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Conflict, "{\"code\":\"LICENSE_INVALID_TRANSITION\",\"detail\":\"A revoked license cannot be activated.\"}");

        (await h.Gateway.GetAsync<Payload>("x")).Error!.Message.ShouldBe("A revoked license cannot be activated.");

        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.BadRequest, "{\"code\":\"VALIDATION_FAILED\",\"detail\":\"" + new string('x', 5000) + "\"}");
        (await h.Gateway.GetAsync<Payload>("x")).Error!.Message.Length.ShouldBeLessThan(400);
    }

    [Fact]
    public async Task A_non_json_gateway_page_does_not_leak_into_the_message()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>nginx 502 secret-upstream-name</html>") };

        var error = (await h.Gateway.GetAsync<Payload>("x")).Error!;

        error.Message.ShouldNotContain("nginx");
        error.Message.ShouldNotContain("secret-upstream-name");
        error.Status.ShouldBe(502);
    }

    [Fact]
    public async Task The_correlation_header_is_used_when_the_body_has_none()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.ServiceUnavailable, "{}", correlation: "hdr-7");

        (await h.Gateway.GetAsync<Payload>("x")).Error!.CorrelationId.ShouldBe("hdr-7");
    }

    [Fact]
    public async Task Network_failures_and_timeouts_are_reported_as_unavailable_not_as_exceptions()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => throw new HttpRequestException("connection refused to 10.0.0.5:443");

        var down = (await h.Gateway.GetAsync<Payload>("x")).Error!;
        down.Code.ShouldBe("API_UNAVAILABLE");
        down.Message.ShouldNotContain("10.0.0.5");

        h.Api.Respond = _ => throw new HttpRequestException("name resolution failed");
        (await h.Gateway.GetAsync<Payload>("x")).Error!.Code.ShouldBe("API_UNAVAILABLE");
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_cancels()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => h.Gateway.GetAsync<Payload>("x", cts.Token));
    }

    [Fact]
    public async Task A_garbled_success_body_is_an_error_not_a_crash()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "this is not json");

        (await h.Gateway.GetAsync<Payload>("x")).Error!.Code.ShouldBe("INTERNAL_ERROR");
    }

    [Fact]
    public async Task Requests_are_camel_case_json_and_enums_travel_as_strings()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"id\":\"" + Guid.NewGuid() + "\",\"key\":\"x\",\"group\":\"g\",\"scope\":\"Both\",\"description\":\"d\"}");

        var result = await h.Gateway.SendAsync<PermissionDto>(HttpMethod.Put, "admin/x", new AdjustLicenseRequest(-5, "typo"));

        result.Value.Scope.ShouldBe(PermissionScopeKind.Both);
        using var body = JsonDocument.Parse(h.Api.Seen.Single().Body!);
        body.RootElement.GetProperty("credits").GetInt32().ShouldBe(-5);
        body.RootElement.GetProperty("reason").GetString().ShouldBe("typo");
    }

    [Fact]
    public async Task Typed_clients_build_the_documented_routes()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"items\":[],\"page\":2,\"pageSize\":10,\"totalCount\":0}");
        var clients = new ClientsApiClient(h.Gateway);
        var licensing = new LicensingApiClient(h.Gateway);

        await clients.ListAsync(new PageRequest { Page = 2, PageSize = 10, Search = "a&b c", Sort = "name:asc" }, "Active");
        await licensing.ListAsync(new PageRequest { Page = 1, PageSize = 25 }, "Expired", Guid.Empty, 30);

        h.Api.Seen[0].Path.ShouldBe("/api/v1/admin/clients?page=2&pageSize=10&sort=name%3Aasc&search=a%26b%20c&status=Active");
        h.Api.Seen[1].Path.ShouldBe($"/api/v1/admin/licenses?page=1&pageSize=25&status=Expired&clientId={Guid.Empty}&expiringInDays=30");
    }

    [Fact]
    public async Task Mutations_use_the_right_verbs_and_paths()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var id = Guid.NewGuid();
        var licensing = new LicensingApiClient(h.Gateway);
        var clients = new ClientsApiClient(h.Gateway);

        await clients.ResetUserPasswordAsync(id, id);
        await licensing.VerifyAllLedgersAsync();

        h.Api.Seen[0].Method.ShouldBe(HttpMethod.Post);
        h.Api.Seen[0].Path.ShouldBe($"/api/v1/admin/clients/{id}/users/{id}/reset-password");
        h.Api.Seen[1].Path.ShouldBe("/api/v1/admin/licensing/verify-ledger");
    }

    [Fact]
    public async Task Login_goes_out_anonymously_and_the_password_is_not_logged_or_echoed_in_errors()
    {
        var h = new BffHarness(sessionId: null);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Unauthorized, "{\"code\":\"UNAUTHENTICATED\",\"detail\":\"The email or password is not correct.\"}", "c-5");
        var auth = new AuthApiClient(h.Gateway);

        var result = await auth.LoginAsync(new LoginRequest("ada@x.test", "Hunter2-Hunter2"));

        h.Api.Seen.Single().Authorization.ShouldBeNull();
        h.Api.Seen.Single().Path.ShouldBe("/api/v1/auth/login");
        result.Error!.Message.ShouldNotContain("Hunter2");
        result.Error.ToString().ShouldNotContain("Hunter2");
        result.Error.CorrelationId.ShouldBe("c-5");
    }

    [Fact]
    public async Task Fetching_the_profile_after_login_uses_the_explicit_token_not_a_session()
    {
        var h = new BffHarness(sessionId: null);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "{\"user\":{\"id\":\"" + Guid.NewGuid() + "\",\"email\":\"a@b.test\",\"fullName\":\"A\",\"isPlatformUser\":true,\"clientId\":null,\"roles\":[\"SuperAdmin\"]},\"permissions\":[\"dashboard.admin\"],\"mustChangePassword\":false,\"client\":null}");

        var me = await new AuthApiClient(h.Gateway).GetMeAsync("tok-123");

        me.Value.Permissions.ShouldBe(["dashboard.admin"]);
        h.Api.Seen.Single().Authorization.ShouldBe("Bearer tok-123");
    }

    [Fact]
    public void Query_strings_drop_empty_values_and_escape_the_rest()
    {
        ApiQuery.With("p", ("a", null), ("b", ""), ("c", "x y"), ("d", 5)).ShouldBe("p?c=x%20y&d=5");
        ApiQuery.With("p", ("a", null)).ShouldBe("p");
    }
}
