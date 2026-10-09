using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace NexaVerify.Web.ComponentTests;

/// <summary>The public website through the real portal host, with a fake public API behind it.</summary>
public class PublicSiteEndToEndTests : IDisposable
{
    private readonly FakePublicApi _api = new();
    private readonly WebApplicationFactory<Program> _factory;

    public PublicSiteEndToEndTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Site:PublicBaseUrl", "https://www.example.test");
            builder.ConfigureTestServices(services => services.AddScoped<IPublicApiClient>(_ => _api));
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/")]
    [InlineData("/features")]
    [InlineData("/how-it-works")]
    [InlineData("/pricing")]
    [InlineData("/developers")]
    [InlineData("/security")]
    [InlineData("/contact")]
    [InlineData("/signup")]
    [InlineData("/verify-email?email=a%40b.test&token=t")]
    [InlineData("/terms")]
    [InlineData("/privacy")]
    public async Task Anonymous_public_pages_are_plain_html_without_a_circuit_and_with_the_security_headers(string path)
    {
        using var client = Client();

        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        html.ShouldNotContain("<!--Blazor:", Case.Sensitive, "static rendering: no interactive component, so no SignalR circuit starts");
        Regex.Matches(html, "<h1[ >]").Count.ShouldBe(1);
        Regex.Matches(html, "<title>").Count.ShouldBe(1);
        html.ShouldContain("rel=\"canonical\"");
        html.ShouldContain("property=\"og:title\"");
        html.ShouldContain("<main id=\"main-content\"");
        html.ShouldContain("class=\"mk-skip\"");
        html.ShouldNotContain("<style");

        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = Regex.Match(csp, "'nonce-([^']+)'").Groups[1].Value;
        WebUtility.HtmlDecode(html).ShouldContain($"nonce=\"{nonce}\"");
        csp.Split("; ").Single(d => d.StartsWith("script-src")).ShouldNotContain("unsafe-inline");
        csp.ShouldNotContain("cloudflare", Case.Sensitive, "no third-party origin unless the captcha is switched on");
        html.ShouldNotContain("google", Case.Insensitive);
    }

    [Fact]
    public async Task Home_links_to_sign_in_and_sign_up_and_lists_the_api_plans()
    {
        using var client = Client();

        var html = await client.GetStringAsync("/");

        html.ShouldContain("href=\"/login\"");
        html.ShouldContain("href=\"/signup\"");
        html.ShouldContain("Free trial");
        html.ShouldContain("200 credits for 14 days");
        html.ShouldContain("\"@type\":\"Organization\"");
    }

    [Fact]
    public async Task Root_no_longer_redirects_to_login()
    {
        using var client = Client();

        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/login")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/client")).StatusCode.ShouldNotBe(HttpStatusCode.OK, "the signed-in areas stay protected");
    }

    [Fact]
    public async Task Unknown_pages_return_404_with_the_friendly_page()
    {
        using var client = Client();

        var response = await client.GetAsync("/definitely/not/here");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        html.ShouldContain("find that page");
        html.ShouldContain("noindex");
    }

    [Fact]
    public async Task Robots_and_sitemap_are_served_and_use_the_configured_address()
    {
        using var client = Client();

        var robots = await client.GetAsync("/robots.txt");
        var robotsText = await robots.Content.ReadAsStringAsync();
        var sitemap = await client.GetAsync("/sitemap.xml");
        var sitemapText = await sitemap.Content.ReadAsStringAsync();

        robots.StatusCode.ShouldBe(HttpStatusCode.OK);
        robots.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        foreach (var path in new[] { "/admin", "/client", "/login" })
        {
            robotsText.ShouldContain($"Disallow: {path}\n");
        }

        robotsText.ShouldContain("Sitemap: https://www.example.test/sitemap.xml");
        sitemap.StatusCode.ShouldBe(HttpStatusCode.OK);
        sitemap.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        sitemapText.ShouldContain("<loc>https://www.example.test/pricing</loc>");
        sitemapText.ShouldNotContain("/admin");
    }

    [Fact]
    public async Task Static_assets_are_served_with_cache_headers()
    {
        using var client = Client();
        var html = await client.GetStringAsync("/");
        var css = Regex.Match(html, "href=\"(marketing[^\"]*\\.css)\"").Groups[1].Value;
        var js = Regex.Match(html, "src=\"(js/site[^\"]*\\.js)\"").Groups[1].Value;

        css.ShouldNotBeNullOrEmpty();
        js.ShouldNotBeNullOrEmpty();
        foreach (var asset in new[] { css, js })
        {
            var response = await client.GetAsync("/" + asset);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.CacheControl.ShouldNotBeNull();
            (response.Headers.ETag is not null || response.Content.Headers.LastModified is not null).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Sign_up_round_trip_through_the_real_form_with_antiforgery()
    {
        using var client = Client();
        var form = await FormFieldsAsync(client, "/signup");

        form["Model.CompanyName"] = "Acme Ltd";
        form["Model.FullName"] = "Ada Lovelace";
        form["Model.Email"] = "ada@acme.test";
        form["Model.Password"] = "correct horse battery staple";
        form["Model.ConfirmPassword"] = "correct horse battery staple";
        form["Model.AcceptTerms"] = "true";
        var response = await client.PostAsync("/signup", new FormUrlEncodedContent(form));
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Check your email");
        html.ShouldNotContain("correct horse battery staple");
        var sent = _api.Signups.ShouldHaveSingleItem();
        sent.Email.ShouldBe("ada@acme.test");
        sent.AcceptTerms.ShouldBeTrue();
        sent.Website.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Sign_up_without_an_antiforgery_token_is_refused()
    {
        using var client = Client();

        var response = await client.PostAsync("/signup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Model.Email"] = "ada@acme.test",
            ["_handler"] = "signup",
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _api.Signups.ShouldBeEmpty();
    }

    [Fact]
    public async Task Honeypot_posted_through_the_real_form_sends_nothing_but_looks_normal()
    {
        using var client = Client();
        var form = await FormFieldsAsync(client, "/signup");
        form["Model.CompanyName"] = "Acme";
        form["Model.FullName"] = "Ada";
        form["Model.Email"] = "bot@acme.test";
        form["Model.Password"] = "correct horse battery staple";
        form["Model.ConfirmPassword"] = "correct horse battery staple";
        form["Model.AcceptTerms"] = "true";
        form["Model.Website"] = "http://spam.example";

        var html = await (await client.PostAsync("/signup", new FormUrlEncodedContent(form))).Content.ReadAsStringAsync();

        html.ShouldContain("Check your email");
        _api.Signups.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closed_sign_ups_show_the_closed_message_from_the_real_host()
    {
        _api.Config = () => ApiResult<PublicConfigDto>.Ok(new PublicConfigDto(false, 0, 0, new CaptchaConfigDto("none", null)));
        using var client = Client();

        var html = await client.GetStringAsync("/signup");

        html.ShouldContain("Sign-ups are currently closed");
        html.ShouldNotContain("name=\"Model.Password\"");
    }

    [Fact]
    public async Task Contact_round_trip_and_verify_email_confirmation()
    {
        using var client = Client();
        var contact = await FormFieldsAsync(client, "/contact");
        contact["Model.Name"] = "Ada";
        contact["Model.Email"] = "ada@acme.test";
        contact["Model.Message"] = "We would like to talk about volumes.";
        var contactHtml = await (await client.PostAsync("/contact", new FormUrlEncodedContent(contact))).Content.ReadAsStringAsync();
        contactHtml.ShouldContain("Thank you");
        _api.Contacts.ShouldHaveSingleItem().Email.ShouldBe("ada@acme.test");

        var verifyUrl = "/verify-email?email=ada%40acme.test&token=tok-1";
        var verify = await FormFieldsAsync(client, verifyUrl);
        var verifyHtml = await (await client.PostAsync(verifyUrl, new FormUrlEncodedContent(verify))).Content.ReadAsStringAsync();
        verifyHtml.ShouldContain("Email confirmed");
        _api.Verifications.ShouldHaveSingleItem().Token.ShouldBe("tok-1");
    }

    [Fact]
    public async Task The_captcha_origin_is_allowed_on_sign_up_only_when_switched_on()
    {
        _api.Config = () => ApiResult<PublicConfigDto>.Ok(new PublicConfigDto(true, 200, 14, new CaptchaConfigDto("turnstile", "site-key")));
        using var off = Client();
        (await off.GetAsync("/signup")).Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain("cloudflare");

        using var onFactory = _factory.WithWebHostBuilder(b => b.UseSetting("Security:Headers:TurnstileEnabled", "true"));
        using var on = onFactory.CreateClient();
        var signup = await on.GetAsync("/signup");
        var html = await signup.Content.ReadAsStringAsync();

        signup.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("script-src 'self' 'nonce-");
        signup.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("https://challenges.cloudflare.com");
        html.ShouldContain("data-sitekey=\"site-key\"");
        (await on.GetAsync("/pricing")).Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain("cloudflare");
    }

    private static async Task<Dictionary<string, string>> FormFieldsAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        var fields = new Dictionary<string, string>();
        foreach (Match input in Regex.Matches(html, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            var name = Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value;
            var value = Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value;
            if (name.Length > 0)
            {
                fields[name] = WebUtility.HtmlDecode(value);
            }
        }

        fields.ShouldContainKey("__RequestVerificationToken", "the form carries an antiforgery token");
        return fields;
    }
}
