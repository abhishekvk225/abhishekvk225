using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Api.IntegrationTests;

public class PipelineTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public PipelineTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task System_info_is_public_and_carries_hardening_headers()
    {
        var response = await _client.GetAsync("/api/v1/system/info");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        response.Headers.GetValues("Permissions-Policy").Single().ShouldContain("camera=()");
        response.Headers.GetValues("Cache-Control").ShouldBe(["no-store"]);
        response.Headers.Contains("Server").ShouldBeFalse();
        response.Headers.Contains(HttpHeaderNames.CorrelationId).ShouldBeTrue();
    }

    [Fact]
    public async Task A_wellformed_correlation_id_is_echoed()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        request.Headers.Add(HttpHeaderNames.CorrelationId, "client-req_42.a");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues(HttpHeaderNames.CorrelationId).ShouldBe(["client-req_42.a"]);
    }

    [Theory]
    [InlineData("has spaces and <script>")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task A_malformed_correlation_id_is_replaced(string supplied)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        request.Headers.TryAddWithoutValidation(HttpHeaderNames.CorrelationId, supplied);

        var response = await _client.SendAsync(request);

        var id = response.Headers.GetValues(HttpHeaderNames.CorrelationId).Single();
        id.ShouldNotBe(supplied);
        id.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_json_with_code_and_correlation_id()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/nope");
        request.Headers.Add(TestAuthHandler.ClientHeader, Guid.NewGuid().ToString());
        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(ErrorCodes.NotFound);
        problem.GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
        response.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
    }

    [Fact]
    public async Task Anonymous_callers_cannot_probe_for_routes()
    {
        // deny-by-default: unknown paths look exactly like protected ones to the unauthenticated
        var response = await _client.GetAsync("/api/v1/nope");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response)).GetProperty("code").GetString().ShouldBe(ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task Unhandled_exceptions_never_leak_details()
    {
        var response = await _client.GetAsync("/test/throw");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("hunter2");
        raw.ShouldNotContain("SELECT");
        raw.ShouldNotContain("InvalidOperationException");
        raw.ShouldNotContain("   at ");
        var problem = JsonDocument.Parse(raw).RootElement;
        problem.GetProperty("code").GetString().ShouldBe(ErrorCodes.InternalError);
        problem.GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Tenant_violations_look_like_not_found_and_leak_nothing()
    {
        var response = await _client.GetAsync("/test/tenant-violation");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("tenant B");
        raw.ShouldContain(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Domain_rule_violations_map_to_conflict_with_their_code()
    {
        var response = await _client.GetAsync("/test/domain");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).GetProperty("code").GetString().ShouldBe(ErrorCodes.LicenseInvalidTransition);
    }

    [Fact]
    public async Task Fluent_validation_failures_return_field_errors()
    {
        var response = await _client.PostAsJsonAsync("/test/fluent", new { name = "", age = 3 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
        var errors = problem.GetProperty("errors");
        errors.GetProperty("Name")[0].GetString().ShouldBe("Name is required.");
        errors.GetProperty("Age")[0].GetString().ShouldBe("Must be an adult.");
    }

    [Fact]
    public async Task Valid_fluent_body_passes_through()
    {
        var response = await _client.PostAsJsonAsync("/test/fluent", new { name = "Ada", age = 30 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Data_annotation_failures_use_the_same_problem_format()
    {
        var response = await _client.PostAsJsonAsync("/test/annotated", new { code = "TOO-LONG-VALUE" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
        problem.GetProperty("errors").TryGetProperty("Code", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("ok", 200, null)]
    [InlineData("validation", 400, "VALIDATION_FAILED")]
    [InlineData("unauth", 401, "API_KEY_INVALID")]
    [InlineData("payment", 402, "LICENSE_INSUFFICIENT_BALANCE")]
    [InlineData("forbidden", 403, "CLIENT_SUSPENDED")]
    [InlineData("notfound", 404, "NOT_FOUND")]
    [InlineData("conflict", 409, "CONFLICT")]
    [InlineData("toomany", 429, "RATE_LIMITED")]
    [InlineData("unavailable", 503, "FACE_PROVIDER_UNAVAILABLE")]
    [InlineData("failure", 500, "INTERNAL_ERROR")]
    public async Task Result_errors_map_to_the_documented_http_status_and_code(string kind, int status, string? code)
    {
        var response = await _client.GetAsync($"/test/result/{kind}");

        ((int)response.StatusCode).ShouldBe(status);
        if (code is not null)
        {
            (await ReadProblemAsync(response)).GetProperty("code").GetString().ShouldBe(code);
        }
    }

    [Fact]
    public async Task Protected_endpoints_reject_anonymous_callers_with_problem_json()
    {
        var response = await _client.GetAsync("/test/protected");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response)).GetProperty("code").GetString().ShouldBe(ErrorCodes.Unauthenticated);
    }

    [Fact]
    public async Task Tenant_is_taken_from_the_credential_not_from_the_request()
    {
        var clientId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/test/whoami?clientId={Guid.NewGuid()}");
        request.Headers.Add(TestAuthHandler.ClientHeader, clientId.ToString());
        request.Headers.Add("X-Client-Id", Guid.NewGuid().ToString()); // spoof attempt

        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("clientId").GetGuid().ShouldBe(clientId);
        body.GetProperty("isPlatform").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Platform_principals_resolve_to_platform_scope_without_a_client()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add(TestAuthHandler.PlatformHeader, "1");

        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("isPlatform").GetBoolean().ShouldBeTrue();
        body.GetProperty("clientId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Liveness_is_ok_without_touching_the_database()
    {
        (await _client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_reports_unavailable_when_the_database_is_unreachable()
    {
        (await _client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task OpenApi_document_and_swagger_ui_are_available_outside_production()
    {
        var doc = await _client.GetAsync("/openapi/v1.json");
        doc.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await doc.Content.ReadAsStringAsync()).ShouldContain("/api/v1/system/info");

        (await _client.GetAsync("/swagger/index.html")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
