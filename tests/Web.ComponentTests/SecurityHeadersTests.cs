using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class SecurityHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SecurityHeadersTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Csp_never_allows_unsafe_inline_scripts_and_carries_the_nonce()
    {
        var csp = SecurityHeadersMiddleware.BuildCsp(new SecurityHeadersOptions(), "abc123");

        var script = csp.Split("; ").Single(d => d.StartsWith("script-src", StringComparison.Ordinal));
        script.ShouldContain("'nonce-abc123'");
        script.ShouldNotContain("unsafe-inline");
        script.ShouldNotContain("unsafe-eval");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("connect-src 'self' wss:");
        csp.ShouldNotContain(" ws:");
    }

    [Fact]
    public async Task Login_page_sends_hardened_headers_with_a_fresh_nonce_matching_its_scripts()
    {
        using var client = _factory.CreateClient();

        var first = await client.GetAsync("/login");
        var second = await client.GetAsync("/login");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.Contains("Server").ShouldBeFalse("Kestrel server header must be off");
        first.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        first.Headers.GetValues("Permissions-Policy").Single().ShouldContain("camera=(self)");
        first.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");

        var csp1 = first.Headers.GetValues("Content-Security-Policy").Single();
        var csp2 = second.Headers.GetValues("Content-Security-Policy").Single();
        csp1.ShouldNotBe(csp2, "nonce must change per request");

        var nonce = System.Text.RegularExpressions.Regex.Match(csp1, "'nonce-([^']+)'").Groups[1].Value;
        var html = System.Net.WebUtility.HtmlDecode(await first.Content.ReadAsStringAsync());
        html.ShouldContain($"nonce=\"{nonce}\"");
        html.ShouldNotContain("<script>");
    }

    [Fact]
    public async Task Antiforgery_cookie_is_http_only_and_same_site_strict()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/login");

        var cookie = response.Headers.GetValues("Set-Cookie").FirstOrDefault(c => c.StartsWith("nv.af=", StringComparison.Ordinal));
        cookie.ShouldNotBeNull();
        cookie.ToLowerInvariant().ShouldContain("httponly");
        cookie.ToLowerInvariant().ShouldContain("samesite=strict");
    }
}
