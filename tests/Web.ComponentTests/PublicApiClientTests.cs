using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

/// <summary>The public API client over a fake HTTP server: exact paths, bodies, anonymity and error mapping.</summary>
public class PublicApiClientTests
{
    private static (PublicApiClient Client, ScriptedApi Api) Create()
    {
        var h = new BffHarness();
        return (new PublicApiClient(h.Gateway), h.Api);
    }

    [Fact]
    public async Task Plans_are_read_anonymously_and_deserialised()
    {
        var (client, api) = Create();
        api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Free trial\",\"description\":null,\"credits\":200,\"validityDays\":14,\"highlights\":[\"a\",\"b\"],\"isTrial\":true,\"displayPrice\":\"Free\"},"
            + "{\"id\":\"22222222-2222-2222-2222-222222222222\",\"name\":\"Pro\",\"credits\":5000,\"validityDays\":365,\"highlights\":[],\"isTrial\":false}]");

        var result = await client.GetPlansAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(2);
        result.Value[0].IsTrial.ShouldBeTrue();
        result.Value[0].Highlights.ShouldBe(["a", "b"]);
        result.Value[1].DisplayPrice.ShouldBeNull();
        var seen = api.Seen.ShouldHaveSingleItem();
        seen.Method.ShouldBe(HttpMethod.Get);
        seen.Path.ShouldBe("/api/v1/public/plans");
        seen.Authorization.ShouldBeNull("public calls never carry credentials");
    }

    [Fact]
    public async Task Config_is_read_including_the_captcha_settings()
    {
        var (client, api) = Create();
        api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"signupEnabled\":true,\"trialCredits\":200,\"trialDays\":14,\"captcha\":{\"provider\":\"turnstile\",\"siteKey\":\"abc\"}}");

        var config = (await client.GetConfigAsync()).Value;

        config.SignupEnabled.ShouldBeTrue();
        config.TrialCredits.ShouldBe(200);
        config.Captcha.IsTurnstile.ShouldBeTrue();
        config.Captcha.SiteKey.ShouldBe("abc");
        api.Seen.Single().Path.ShouldBe("/api/v1/public/config");
    }

    [Fact]
    public async Task Sign_up_posts_the_documented_body_and_accepts_202()
    {
        var (client, api) = Create();
        api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Accepted, "{}");

        var result = await client.SignupAsync(new SignupRequest("Acme", "Ada", "ada@acme.test", "pw-pw-pw-pw-pw", true, "cap-token", null));

        result.IsSuccess.ShouldBeTrue();
        var seen = api.Seen.Single();
        seen.Method.ShouldBe(HttpMethod.Post);
        seen.Path.ShouldBe("/api/v1/public/signup");
        seen.Authorization.ShouldBeNull();
        using var body = JsonDocument.Parse(seen.Body!);
        body.RootElement.GetProperty("companyName").GetString().ShouldBe("Acme");
        body.RootElement.GetProperty("fullName").GetString().ShouldBe("Ada");
        body.RootElement.GetProperty("email").GetString().ShouldBe("ada@acme.test");
        body.RootElement.GetProperty("password").GetString().ShouldBe("pw-pw-pw-pw-pw");
        body.RootElement.GetProperty("acceptTerms").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("captchaToken").GetString().ShouldBe("cap-token");
    }

    [Fact]
    public async Task Sign_up_validation_problems_become_field_errors_and_429_is_friendly()
    {
        var (client, api) = Create();
        api.Respond = _ => ScriptedApi.Json(HttpStatusCode.BadRequest,
            "{\"code\":\"VALIDATION_FAILED\",\"detail\":\"One or more validation errors occurred.\",\"errors\":{\"password\":[\"Too common.\"]}}", "corr-9");

        var invalid = (await client.SignupAsync(new SignupRequest("A", "B", "c@d.test", "x", true))).Error!;

        invalid.Status.ShouldBe(400);
        invalid.FieldErrors!["password"].ShouldBe(["Too common."]);
        invalid.CorrelationId.ShouldBe("corr-9");

        api.Respond = _ => ScriptedApi.Json(HttpStatusCode.TooManyRequests, "{\"code\":\"RATE_LIMITED\"}");
        var limited = (await client.SignupAsync(new SignupRequest("A", "B", "c@d.test", "x", true))).Error!;
        limited.Status.ShouldBe(429);
        limited.Message.ShouldBe("Too many requests right now. Please wait a moment and try again.");
    }

    [Fact]
    public async Task Verify_posts_email_and_token_and_accepts_204()
    {
        var (client, api) = Create();
        api.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await client.VerifySignupAsync(new VerifySignupRequest("ada@acme.test", "tok"));

        result.IsSuccess.ShouldBeTrue();
        var seen = api.Seen.Single();
        seen.Path.ShouldBe("/api/v1/public/signup/verify");
        using var body = JsonDocument.Parse(seen.Body!);
        body.RootElement.GetProperty("email").GetString().ShouldBe("ada@acme.test");
        body.RootElement.GetProperty("token").GetString().ShouldBe("tok");
    }

    [Fact]
    public async Task Verify_failure_is_a_failed_result_not_an_exception()
    {
        var (client, api) = Create();
        api.Respond = _ => ScriptedApi.Json(HttpStatusCode.BadRequest, "{\"code\":\"VALIDATION_FAILED\"}");

        (await client.VerifySignupAsync(new VerifySignupRequest("a@b.test", "t"))).Error!.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Contact_posts_the_documented_body_and_accepts_202()
    {
        var (client, api) = Create();
        api.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted);

        var result = await client.ContactAsync(new ContactRequest("Ada", "ada@acme.test", null, "Hello there, friends"));

        result.IsSuccess.ShouldBeTrue();
        var seen = api.Seen.Single();
        seen.Path.ShouldBe("/api/v1/public/contact");
        using var body = JsonDocument.Parse(seen.Body!);
        body.RootElement.GetProperty("name").GetString().ShouldBe("Ada");
        body.RootElement.GetProperty("message").GetString().ShouldBe("Hello there, friends");
    }

    [Fact]
    public async Task An_unreachable_api_is_a_friendly_failure()
    {
        var (client, api) = Create();
        api.Respond = _ => throw new HttpRequestException("boom");

        var error = (await client.GetPlansAsync()).Error!;

        error.Status.ShouldBe(503);
        error.Message.ShouldNotContain("boom");
    }

    [Fact]
    public async Task The_end_users_address_is_forwarded_for_the_apis_per_address_limits()
    {
        var h = new BffHarness(address: new ClientAddress { Value = "203.0.113.7" });
        h.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted);

        await new PublicApiClient(h.Gateway).ContactAsync(new ContactRequest("A", "a@b.test", null, "0123456789"));

        h.Api.Seen.Single().ForwardedFor.ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task Cache_keeps_successful_plans_and_config_but_never_failures_or_mutations()
    {
        var fake = new FakePublicApi();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cached = new CachingPublicApiClient(fake, memory);

        await cached.GetPlansAsync();
        await cached.GetPlansAsync();
        fake.PlanCalls.ShouldBe(1);

        using var memory2 = new MemoryCache(new MemoryCacheOptions());
        var failing = new FakePublicApi { Plans = () => ApiResult<IReadOnlyList<PublicPlanDto>>.Fail("INTERNAL_ERROR", "x", null, 500) };
        var cached2 = new CachingPublicApiClient(failing, memory2);
        await cached2.GetPlansAsync();
        await cached2.GetPlansAsync();
        failing.PlanCalls.ShouldBe(2, "failures are retried, not cached");

        await cached.SignupAsync(new SignupRequest("A", "B", "c@d.test", "p", true));
        await cached.SignupAsync(new SignupRequest("A", "B", "c@d.test", "p", true));
        fake.Signups.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_stub_never_needs_an_api_and_fails_on_request()
    {
        var stub = new StubPublicApiClient();

        (await stub.GetPlansAsync()).Value.ShouldNotBeEmpty();
        (await stub.GetConfigAsync()).Value.SignupEnabled.ShouldBeTrue();
        (await stub.SignupAsync(new SignupRequest("A", "B", "fail@x.test", "p", true))).IsSuccess.ShouldBeFalse();
        (await stub.VerifySignupAsync(new VerifySignupRequest("a@b.test", "bad"))).IsSuccess.ShouldBeFalse();
    }
}

public class PublicSiteInfrastructureTests
{
    private static SiteUrls Urls(string? baseUrl) => new(Options.Create(new SiteOptions { PublicBaseUrl = baseUrl ?? string.Empty }));

    [Fact]
    public void Sitemap_lists_public_pages_with_the_configured_address_and_leaves_out_private_ones()
    {
        var xml = SeoEndpoints.BuildSitemap(Urls("https://www.example.test/"), "http://evil.example/");

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var locs = doc.Descendants().Where(e => e.Name.LocalName == "loc").Select(e => e.Value).ToList();
        locs.ShouldContain("https://www.example.test/");
        locs.ShouldContain("https://www.example.test/pricing");
        locs.ShouldContain("https://www.example.test/signup");
        locs.ShouldNotContain(l => l.Contains("evil"));
        locs.ShouldNotContain(l => l.Contains("/admin") || l.Contains("/client") || l.Contains("/login") || l.Contains("verify-email") || l.Contains("not-found"));
        doc.Root!.Name.NamespaceName.ShouldBe("http://www.sitemaps.org/schemas/sitemap/0.9");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://x.test")]
    [InlineData("https://user:pw@x.test")]
    public void A_bad_configured_address_is_ignored_in_favour_of_the_request(string configured)
    {
        Urls(configured).Absolute("/pricing", "https://req.example/").ShouldBe("https://req.example/pricing");
    }

    [Fact]
    public void A_configured_path_prefix_is_kept()
    {
        Urls("https://example.test/site").Absolute("/pricing", null).ShouldBe("https://example.test/site/pricing");
        Urls("https://example.test/site/").Absolute("/", null).ShouldBe("https://example.test/site/");
    }

    [Theory]
    [InlineData("/", true, true)]
    [InlineData("/features", true, true)]
    [InlineData("/pricing/", true, true)]
    [InlineData("/verify-email", true, true)]
    [InlineData("/not-found", true, true)]
    [InlineData("/login", false, true)]
    [InlineData("/login/mfa", false, true)]
    [InlineData("/client", false, false)]
    [InlineData("/client/api-docs", false, false)]
    [InlineData("/admin", false, false)]
    [InlineData("/forgot-password", false, false)]
    [InlineData("/security-x", false, false)]
    public void Only_the_public_pages_and_sign_in_are_static_server_rendered(string path, bool marketing, bool isStatic)
    {
        PublicRoutes.IsMarketing(path).ShouldBe(marketing);
        PublicRoutes.IsStatic(path).ShouldBe(isStatic);
    }

    [Fact]
    public void Csp_allows_the_bot_check_origin_only_when_asked_and_never_unsafe_inline_scripts()
    {
        var options = new SecurityHeadersOptions();

        var plain = SecurityHeadersMiddleware.BuildCsp(options, "n");
        var captcha = SecurityHeadersMiddleware.BuildCsp(options, "n", allowCaptcha: true);

        plain.ShouldNotContain("cloudflare");
        plain.ShouldNotContain("frame-src");
        captcha.Split("; ").Single(d => d.StartsWith("script-src")).ShouldContain("https://challenges.cloudflare.com");
        captcha.ShouldContain("frame-src https://challenges.cloudflare.com");
        captcha.Split("; ").Single(d => d.StartsWith("script-src")).ShouldNotContain("unsafe-inline");
        captcha.ShouldContain("form-action 'self'");
    }
}
