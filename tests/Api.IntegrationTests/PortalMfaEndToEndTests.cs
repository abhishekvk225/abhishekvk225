extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Identity;
using NexaVerify.Contracts.Identity;
using NexaVerify.TestSupport;
using WebApp::NexaVerify.Web.Security;
using WebApp::NexaVerify.Web.Services;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>The portal's two-step sign-in against the REAL API: the challenge stays on the server, the browser only ever holds opaque cookies.</summary>
[Collection(SqlServerCollection.Name)]
public class PortalMfaEndToEndTests : IAsyncLifetime
{
    private static readonly Uri Origin = new("https://portal.test");
    private readonly SqlServerFixture _fixture;
    private AuthApp _api = null!;
    private WebApplicationFactory<WebApp::Program> _portal = null!;

    public PortalMfaEndToEndTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _api = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Mfa:RequiredPlatformRoles"] = "-" });
        _portal = new WebApplicationFactory<WebApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Api:BaseUrl", "https://api.test");
            builder.UseSetting("AllowedHosts", "portal.test");
            builder.ConfigureServices(services =>
            {
                foreach (var name in new[] { ApiClientNames.Authenticated, ApiClientNames.Anonymous })
                {
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => _api.Factory.Server.CreateHandler());
                }
            });
        });
    }

    public async Task DisposeAsync()
    {
        await _portal.DisposeAsync();
        await _api.DisposeAsync();
    }

    private sealed class Browser(HttpClient client)
    {
        public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

        public List<string> Bodies { get; } = [];

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

        public Task<HttpResponseMessage> PostAsync(string url, Dictionary<string, string> form) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) });

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            if (Cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var header in values)
                {
                    var first = header.Split(';')[0];
                    var eq = first.IndexOf('=');
                    var name = first[..eq];
                    var value = first[(eq + 1)..];
                    if (header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
                    {
                        Cookies.Remove(name);
                    }
                    else
                    {
                        Cookies[name] = value;
                    }
                }
            }

            if (response.Content.Headers.ContentType?.MediaType is "text/html")
            {
                Bodies.Add(await response.Content.ReadAsStringAsync());
            }

            return response;
        }
    }

    private Browser NewBrowser() => new(_portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = Origin }));

    private static string Rel(Uri? location) => location!.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;

    private static async Task<string> TokenAsync(Browser browser, string page)
    {
        var html = await (await browser.GetAsync(page)).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.ShouldBeTrue("the form must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> SignInAsync(Browser browser, string email, string password) =>
        await browser.PostAsync("/auth/login", new Dictionary<string, string> { ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = await TokenAsync(browser, "/login") });

    private static async Task<HttpResponseMessage> SecondStepAsync(Browser browser, string code, string? token = null) =>
        await browser.PostAsync("/auth/mfa", new Dictionary<string, string> { ["code"] = code, ["__RequestVerificationToken"] = token ?? await TokenAsync(browser, "/login/mfa") });

    /// <summary>The seeded Super Admin with MFA switched on through the API; returns the base32 secret.</summary>
    private async Task<string> SuperAdminWithMfaAsync()
    {
        var session = await _api.SuperAdminAsync();
        var start = (await (await _api.PostAsync("/api/v1/auth/mfa/enroll", null, session.AccessToken)).Content.ReadFromJsonAsync<MfaEnrolmentDto>(AuthApp.Json))!;
        (await _api.PostAsync("/api/v1/auth/mfa/enroll/confirm",
            new ConfirmMfaRequest(Totp.Compute(Base32.Decode(start.SecretBase32)!, Totp.StepAt(DateTime.UtcNow))), session.AccessToken)).EnsureSuccessStatusCode();
        return start.SecretBase32;
    }

    private async Task<long> LastStepAsync() =>
        (await _api.WithDbAsync(db => db.UserMfa.AsNoTracking().SingleAsync())).LastUsedStep!.Value;

    [Fact]
    public async Task The_password_step_parks_the_challenge_on_the_server_and_the_second_step_creates_the_session()
    {
        var secret = await SuperAdminWithMfaAsync();
        var used = await LastStepAsync();
        var browser = NewBrowser();

        var first = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword);

        first.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(first.Headers.Location).ShouldBe("/login/mfa");
        var cookie = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-nv.mfa=", StringComparison.Ordinal)).ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=strict");
        browser.Cookies.Keys.ShouldNotContain("__Host-nv.session"); // no session yet: nothing is signed in
        var challenge = await _api.WithDbAsync(db => db.MfaChallenges.CountAsync());
        challenge.ShouldBe(1);

        // a visitor with the password but no second factor cannot reach the console
        var console = await browser.GetAsync("/admin");
        console.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(console.Headers.Location).ShouldStartWith("/login?returnUrl=");

        // the second-step page is reachable and posts to the BFF
        var page = await browser.GetAsync("/login/mfa");
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("action=\"/auth/mfa\"");

        // a wrong code goes back to the same page; the right one signs in
        var wrong = await SecondStepAsync(browser, "000000");
        Rel(wrong.Headers.Location).ShouldBe("/login/mfa?error=mfa-invalid");
        var right = await SecondStepAsync(browser, Totp.Compute(Base32.Decode(secret)!, used + 1));
        right.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(right.Headers.Location).ShouldBe("/admin");
        right.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("__Host-nv.session=", StringComparison.Ordinal));
        browser.Cookies.Keys.ShouldNotContain("__Host-nv.mfa"); // the parked step is gone
        (await browser.GetAsync("/admin")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // nothing the browser ever received contains a token or the challenge
        browser.Bodies.ShouldNotBeEmpty();
        browser.Cookies.Values.ShouldAllBe(v => !v.StartsWith("eyJ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_password_never_reaches_the_second_step_and_a_replayed_code_is_refused()
    {
        var secret = await SuperAdminWithMfaAsync();
        var used = await LastStepAsync();
        var browser = NewBrowser();

        var wrong = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, "Wrong-Passphrase-1");
        Rel(wrong.Headers.Location).ShouldContain("error=invalid");
        browser.Cookies.Keys.ShouldNotContain("__Host-nv.mfa");

        // the code used when MFA was switched on cannot be used again
        await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword);
        var replay = await SecondStepAsync(browser, Totp.Compute(Base32.Decode(secret)!, used));
        Rel(replay.Headers.Location).ShouldBe("/login/mfa?error=mfa-invalid");
        browser.Cookies.Keys.ShouldNotContain("__Host-nv.session");
    }

    [Fact]
    public async Task A_challenge_that_ran_out_of_attempts_sends_the_person_back_to_the_password_step()
    {
        await SuperAdminWithMfaAsync();
        var browser = NewBrowser();
        await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword);

        var token = await TokenAsync(browser, "/login/mfa");
        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await SecondStepAsync(browser, "000000", token);
        }

        Rel(last.Headers.Location).ShouldStartWith("/login?error=mfa-expired");
        browser.Cookies.Keys.ShouldNotContain("__Host-nv.mfa");
        var page = await browser.GetAsync("/login/mfa"); // nothing is parked any more: the page itself bounces back to the password step
        page.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(page.Headers.Location).ShouldStartWith("/login?error=mfa-expired");
    }

    [Fact]
    public async Task The_second_step_needs_the_antiforgery_token_and_a_parked_challenge()
    {
        await SuperAdminWithMfaAsync();
        var browser = NewBrowser();
        await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword);

        var forged = await SecondStepAsync(browser, "123456", token: "forged");
        Rel(forged.Headers.Location).ShouldContain("error=expired");

        var stranger = NewBrowser(); // no cookie: nothing is parked for this browser
        var none = await SecondStepAsync(stranger, "123456", token: await TokenAsync(stranger, "/login"));
        Rel(none.Headers.Location).ShouldContain("error=mfa-expired");
    }

    [Fact]
    public async Task An_account_that_must_enrol_is_sent_to_the_enrolment_page_and_nowhere_else()
    {
        await using var strictApi = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Mfa:RequiredPlatformRoles"] = "SuperAdmin" });
        var seeded = await strictApi.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);
        (await strictApi.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, AuthApp.StrongPassword), seeded.AccessToken)).EnsureSuccessStatusCode();
        await using var portal = new WebApplicationFactory<WebApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Api:BaseUrl", "https://api.test");
            builder.UseSetting("AllowedHosts", "portal.test");
            builder.ConfigureServices(services =>
            {
                foreach (var name in new[] { ApiClientNames.Authenticated, ApiClientNames.Anonymous })
                {
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => strictApi.Factory.Server.CreateHandler());
                }
            });
        });
        var browser = new Browser(portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = Origin }));

        var login = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword);

        Rel(login.Headers.Location).ShouldBe("/mfa/enroll");
        var console = await browser.GetAsync("/admin");
        Rel(console.Headers.Location).ShouldBe("/mfa/enroll");
        var clients = await browser.GetAsync("/admin/clients");
        Rel(clients.Headers.Location).ShouldBe("/mfa/enroll");
        (await browser.GetAsync("/mfa/enroll")).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.Cookies.ShouldContainKey("__Host-nv.session");
    }
}
