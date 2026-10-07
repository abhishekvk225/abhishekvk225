using System.Net;
using NexaVerify.Api.IntegrationTests.Support;

namespace NexaVerify.Api.IntegrationTests;

public class HostingTests
{
    [Fact]
    public async Task Openapi_and_swagger_are_not_exposed_in_production()
    {
        await using var factory = new ApiFactory
        {
            Environment = "Production",
            Settings = new Dictionary<string, string> { ["Hosting:RedirectToHttps"] = "false" },
        };
        var client = factory.CreateClient();

        // not mapped => falls through to the deny-by-default policy (401) or 404; never 200
        (await client.GetAsync("/openapi/v1.json")).StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
        (await client.GetAsync("/swagger/index.html")).StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Startup_fails_fast_when_the_connection_string_is_missing()
    {
        using var factory = new ApiFactory { ConnectionString = string.Empty };

        var ex = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        ex.Message.ShouldContain("ConnectionStrings:Default");
    }

    [Fact]
    public void Startup_rejects_wildcard_cors_origins()
    {
        using var factory = new ApiFactory
        {
            Settings = new Dictionary<string, string> { ["Cors:AllowedOrigins:0"] = "*" },
        };

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("explicit origins");
    }

    [Fact]
    public async Task Cors_allows_only_configured_origins()
    {
        await using var factory = new ApiFactory
        {
            Settings = new Dictionary<string, string> { ["Cors:AllowedOrigins:0"] = "https://portal.example.com" },
        };
        var client = factory.CreateClient();

        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        allowed.Headers.Add("Origin", "https://portal.example.com");
        var denied = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        denied.Headers.Add("Origin", "https://evil.example.com");

        (await client.SendAsync(allowed)).Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://portal.example.com"]);
        (await client.SendAsync(denied)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Default_scheme_denies_everything_that_needs_authentication()
    {
        await using var factory = new ApiFactory { UseTestAuth = false };
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/test/whoami");
        request.Headers.Add(TestAuthHandler.ClientHeader, Guid.NewGuid().ToString()); // header means nothing without the test scheme

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
