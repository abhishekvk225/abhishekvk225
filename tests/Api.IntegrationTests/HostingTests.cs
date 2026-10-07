using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;

namespace NexaVerify.Api.IntegrationTests;

public class HostingTests
{
    private static IReadOnlyDictionary<string, string> Prod(params (string Key, string Value)[] extra) =>
        new Dictionary<string, string>(new[] { ("Hosting:RedirectToHttps", "false"), ("AllowedHosts", "api.example.com") }.Concat(extra)
            .Select(t => new KeyValuePair<string, string>(t.Item1, t.Item2)));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Openapi_and_swagger_exist_only_in_development(string environment)
    {
        await using var factory = new ApiFactory { Environment = environment, Settings = Prod(), UseTestAuth = false };
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = "api.example.com";

        // not mapped => deny-by-default policy answers 401
        (await client.GetAsync("/openapi/v1.json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/swagger/index.html")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Production_refuses_to_start_without_real_allowed_hosts()
    {
        using var factory = new ApiFactory
        {
            Environment = "Production",
            Settings = new Dictionary<string, string> { ["Hosting:RedirectToHttps"] = "false", ["AllowedHosts"] = "*" },
        };

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("AllowedHosts");
    }

    [Fact]
    public void Startup_fails_fast_when_the_connection_string_is_missing()
    {
        using var factory = new ApiFactory { ConnectionString = string.Empty };

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("ConnectionStrings:Default");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://portal.example.com")]
    [InlineData("https://portal.example.com/")]
    [InlineData("https://portal.example.com/path")]
    [InlineData("null")]
    public void Startup_rejects_unsafe_cors_origins(string origin)
    {
        using var factory = new ApiFactory { Settings = new Dictionary<string, string> { ["Cors:AllowedOrigins:0"] = origin } };

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("Cors:AllowedOrigins");
    }

    [Fact]
    public async Task Cors_allows_only_configured_origins_including_preflight()
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
        var preflightOk = new HttpRequestMessage(HttpMethod.Options, "/api/v1/system/info");
        preflightOk.Headers.Add("Origin", "https://portal.example.com");
        preflightOk.Headers.Add("Access-Control-Request-Method", "GET");
        var preflightBad = new HttpRequestMessage(HttpMethod.Options, "/api/v1/system/info");
        preflightBad.Headers.Add("Origin", "https://evil.example.com");
        preflightBad.Headers.Add("Access-Control-Request-Method", "GET");

        (await client.SendAsync(allowed)).Headers.GetValues("Access-Control-Allow-Origin").ShouldBe(["https://portal.example.com"]);
        (await client.SendAsync(denied)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        (await client.SendAsync(preflightOk)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeTrue();
        (await client.SendAsync(preflightBad)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
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

    [Fact]
    public async Task Plain_http_is_redirected_to_https_but_health_probes_are_exempt()
    {
        await using var factory = new ApiFactory
        {
            Environment = "Production",
            Settings = new Dictionary<string, string> { ["AllowedHosts"] = "api.example.com", ["Hosting:RedirectToHttps"] = "true" },
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://api.example.com") });

        var redirect = await client.GetAsync("/api/v1/system/info?x=1");
        redirect.StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location!.ToString().ShouldBe("https://api.example.com/api/v1/system/info?x=1");
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Forwarded_proto_from_a_trusted_proxy_enables_hsts_and_suppresses_the_redirect()
    {
        await using var factory = new ApiFactory
        {
            Environment = "Production",
            Settings = new Dictionary<string, string>
            {
                ["AllowedHosts"] = "api.example.com",
                ["Hosting:RedirectToHttps"] = "true",
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
            },
            ConfigureServices = services => services.AddTransient<IStartupFilter, FakeRemoteIpStartupFilter>(),
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://api.example.com") });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Strict-Transport-Security").Single().ShouldContain("max-age=31536000");
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_sender_are_ignored()
    {
        await using var factory = new ApiFactory
        {
            Environment = "Production",
            Settings = new Dictionary<string, string>
            {
                ["AllowedHosts"] = "api.example.com",
                ["Hosting:RedirectToHttps"] = "true",
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:KnownProxies:0"] = "10.9.9.9", // the test client is 127.0.0.1, not trusted
            },
            ConfigureServices = services => services.AddTransient<IStartupFilter, FakeRemoteIpStartupFilter>(),
        };
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("http://api.example.com") });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        request.Headers.Add("X-Forwarded-Proto", "https");

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect);
    }

    [Fact]
    public void Forwarded_headers_require_an_explicit_trust_list()
    {
        using var factory = new ApiFactory { Settings = new Dictionary<string, string> { ["ForwardedHeaders:Enabled"] = "true" } };

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("KnownProxies");
    }

    [Fact]
    public async Task Auth_style_endpoints_are_rate_limited_per_ip_with_a_problem_response()
    {
        await using var factory = new ApiFactory
        {
            Settings = new Dictionary<string, string> { ["RateLimiting:PerIpPerMinute"] = "3" },
        };
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/api/v1/system/info")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await client.GetAsync("/api/v1/system/info");
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Retry-After").ShouldBeTrue();
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().ShouldBe("RATE_LIMITED");
    }

    [Fact]
    public async Task Framework_generated_statuses_keep_their_specific_stable_codes()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var auth = TestAuthHandler.ClientHeader;

        var wrongMethod = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/system/info");
        wrongMethod.Headers.Add(auth, Guid.NewGuid().ToString());
        var methodResponse = await client.SendAsync(wrongMethod);
        methodResponse.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        var wrongType = new HttpRequestMessage(HttpMethod.Post, "/test/fluent") { Content = new StringContent("x", System.Text.Encoding.UTF8, "text/plain") };
        var typeResponse = await client.SendAsync(wrongType);
        typeResponse.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await typeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("UNSUPPORTED_MEDIA_TYPE");
    }
}
