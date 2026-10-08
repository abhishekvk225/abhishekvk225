extern alias WebApp;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Faces;
using NexaVerify.TestSupport;
using WebApp::NexaVerify.Web.Components;
using WebApp::NexaVerify.Web.Security;
using WebApp::NexaVerify.Web.Services;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>
/// The portal (backend-for-frontend) against the REAL API on a real database: sign in through the HTML form, use the session,
/// download through the BFF, refresh tokens, sign out. Every browser-facing response is scanned for the JWT and the refresh token.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class PortalBffEndToEndTests : IAsyncLifetime
{
    private static readonly Uri Origin = new("https://portal.test");
    private readonly SqlServerFixture _fixture;
    private AuthApp _api = null!;
    private WebApplicationFactory<WebApp::Program> _portal = null!;

    public PortalBffEndToEndTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _api = await AuthApp.CreateAsync(_fixture);
        _portal = PortalFor(_api);
    }

    private static WebApplicationFactory<WebApp::Program> PortalFor(AuthApp api) =>
        new WebApplicationFactory<WebApp::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Api:BaseUrl", "https://api.test");
            builder.UseSetting("AllowedHosts", "portal.test");
            builder.ConfigureServices(services =>
            {
                // The portal's HttpClients talk to the in-memory API host instead of the network.
                foreach (var name in new[] { ApiClientNames.Authenticated, ApiClientNames.Anonymous })
                {
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => api.Factory.Server.CreateHandler());
                }
            });
        });

    public async Task DisposeAsync()
    {
        await _portal.DisposeAsync();
        await _api.DisposeAsync();
    }

    private TestBrowser Browser() => new(_portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = Origin }));

    private static async Task<string> AntiforgeryTokenAsync(TestBrowser browser)
    {
        var html = await (await browser.GetAsync("/login")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.ShouldBeTrue("the sign-in form must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> SignInAsync(TestBrowser browser, string email, string password, string? returnUrl = null, string? token = null)
    {
        token ??= await AntiforgeryTokenAsync(browser);
        var form = new Dictionary<string, string> { ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = token };
        if (returnUrl is not null)
        {
            form["returnUrl"] = returnUrl;
        }

        return await browser.PostAsync("/auth/login", new FormUrlEncodedContent(form));
    }

    private string SessionIdOf(TestBrowser browser)
    {
        var cookie = _portal.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = cookie.TicketDataFormat.Unprotect(browser.Cookies[cookie.Cookie.Name!]);
        ticket.ShouldNotBeNull();
        return ticket.Principal.FindFirst(PortalClaims.SessionId)!.Value;
    }

    private static string Rel(Uri? location) => location!.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;

    private static string WithoutReference(Uri? location) => Regex.Replace(Rel(location), "&ref=[^&]*", string.Empty);

    /// <summary>A minimal cookie-keeping client: sends cookies back like a browser (the Secure cookie only over https).</summary>
    private sealed class TestBrowser(HttpClient client)
    {
        public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

        /// <summary>What a browser reports for a request the page itself made (links, form posts); set to "cross-site" to play another website.</summary>
        public string FetchSite { get; set; } = "same-origin";

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content) => SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = content });

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            request.Headers.Add("Sec-Fetch-Site", FetchSite);
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
                    var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                    if (expired || value.Length == 0)
                    {
                        Cookies.Remove(name);
                    }
                    else
                    {
                        Cookies[name] = value;
                    }
                }
            }

            return response;
        }
    }

    [Fact]
    public async Task Login_then_dashboard_then_csv_then_logout_never_exposes_tokens_to_the_browser()
    {
        await _api.SuperAdminAsync(); // changes the seeded password so the account is fully usable
        var browser = Browser();
        var leaks = new List<string>();

        // 1. anonymous visitors cannot reach the console; the redirect keeps the target for later
        var anonymous = await browser.GetAsync("/admin");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(anonymous.Headers.Location).ShouldStartWith("/login?returnUrl=");
        (await browser.GetAsync("/bff/reports/usage.csv")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // 2. the form refuses a missing/forged antiforgery token and a wrong password looks like any other failure
        var forged = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword, token: "forged");
        Rel(forged.Headers.Location).ShouldContain("error=expired");
        var wrong = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, "Wrong-Password-123");
        wrong.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(wrong.Headers.Location).ShouldContain("error=invalid");
        var unknown = await SignInAsync(browser, "nobody@nowhere.test", "Wrong-Password-123");
        WithoutReference(unknown.Headers.Location).ShouldBe(WithoutReference(wrong.Headers.Location), "unknown accounts and wrong passwords must be indistinguishable");

        // 3. a real sign-in: redirect to the requested page inside the portal, an opaque HttpOnly cookie, no token anywhere
        var login = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword, returnUrl: "/admin/clients");
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(login.Headers.Location).ShouldBe("/admin/clients");
        var setCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-nv.session=", StringComparison.Ordinal)).ToLowerInvariant();
        setCookie.ShouldContain("httponly");
        setCookie.ShouldContain("samesite=strict");
        setCookie.ShouldContain("secure");
        setCookie.Contains("expires=").ShouldBeFalse("session cookie: gone when the browser closes");
        Collect(leaks, login);

        // 4. open redirects are refused: a foreign return URL falls back to the portal home
        var other = Browser();
        var evil = await SignInAsync(other, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword, returnUrl: "//evil.example/steal");
        Rel(evil.Headers.Location).ShouldBe("/admin");
        (await other.GetAsync("/auth/signed-out")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // 5. the console shell loads for the signed-in user
        var shell = await browser.GetAsync("/admin");
        shell.StatusCode.ShouldBe(HttpStatusCode.OK);
        Collect(leaks, shell, await shell.Content.ReadAsStringAsync());

        // 6. dashboard data flows browser-session -> BFF -> API (token attached server-side only)
        var sessionId = SessionIdOf(browser);
        string accessToken, refreshToken;
        await using (var scope = _portal.Services.CreateAsyncScope())
        {
            var session = (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId))!;
            accessToken = session.AccessToken;
            refreshToken = session.RefreshToken;
            session.Portal.ShouldBe("admin");
            session.Permissions.ShouldContain(WebPermissions.DashboardAdmin);

            var gateway = scope.ServiceProvider.GetRequiredService<IApiGateway>();
            var dashboard = await gateway.GetAsync<AdminDashboardDto>("admin/dashboard?days=7", default, new ApiCallOptions { SessionId = sessionId });
            dashboard.IsSuccess.ShouldBeTrue(dashboard.Error?.Message);
            dashboard.Value.Days.ShouldBe(7);
            DashboardMapper.ToModel(dashboard.Value).Kpis.Count.ShouldBeGreaterThan(0);
        }

        // 7. CSV download is streamed through the BFF with the right headers, and a bad date is rejected before reaching the API
        var csv = await browser.GetAsync("/bff/reports/usage.csv?from=2026-01-01&to=2026-01-31");
        csv.StatusCode.ShouldBe(HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        csv.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        var csvBody = await csv.Content.ReadAsStringAsync();
        csvBody.ShouldContain(",");
        Collect(leaks, csv, csvBody);
        (await browser.GetAsync("/bff/reports/usage.csv?from=not-a-date")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // 8. sign out: session gone, cookie cleared, refresh token revoked at the API
        var signedOut = await browser.GetAsync("/auth/signed-out");
        signedOut.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(signedOut.Headers.Location).ShouldBe("/login?signedOut=1");
        Collect(leaks, signedOut);
        await using (var scope = _portal.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId)).ShouldBeNull();
        }

        (await browser.GetAsync("/bff/reports/usage.csv")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var refreshAfterLogout = await _api.PostAsync("/api/v1/auth/refresh", new NexaVerify.Contracts.Identity.RefreshRequest(refreshToken));
        refreshAfterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // nothing the browser ever received contained either token
        foreach (var seen in leaks)
        {
            seen.ShouldNotContain(accessToken);
            seen.ShouldNotContain(refreshToken);
            seen.ShouldNotContain("eyJ"); // any JWT
        }
    }

    [Fact]
    public async Task A_forced_password_change_is_enforced_and_then_released()
    {
        var browser = Browser();
        var login = await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);
        Rel(login.Headers.Location).ShouldBe("/change-password");

        // every page redirects to the change-password page while the flag is set
        var admin = await browser.GetAsync("/admin");
        admin.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Rel(admin.Headers.Location).ShouldBe("/change-password");
        (await browser.GetAsync("/change-password")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessionId = SessionIdOf(browser);
        await using var scope = _portal.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPortalAuth>();
        (await auth.ChangePasswordAsync(sessionId, new ChangePasswordModel { CurrentPassword = "wrong", NewPassword = AuthApp.StrongPassword, ConfirmPassword = AuthApp.StrongPassword }))
            .IsSuccess.ShouldBeFalse();
        (await auth.ChangePasswordAsync(sessionId, new ChangePasswordModel
        {
            CurrentPassword = DatabaseBootstrap.SuperAdminPassword, NewPassword = AuthApp.StrongPassword, ConfirmPassword = AuthApp.StrongPassword,
        })).IsSuccess.ShouldBeTrue();

        var session = (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId))!;
        session.MustChangePassword.ShouldBeFalse();
        (await browser.GetAsync("/admin")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Expired_access_tokens_are_refreshed_once_and_a_replayed_refresh_token_ends_the_session()
    {
        await _api.SuperAdminAsync();
        await using var scope = _portal.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPortalAuth>();
        var store = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var gateway = scope.ServiceProvider.GetRequiredService<IApiGateway>();

        var signIn = await auth.SignInAsync(DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword, default, "198.51.100.20");
        signIn.IsSuccess.ShouldBeTrue(signIn.Error?.Message);
        var sid = signIn.Value.Id;
        var options = new ApiCallOptions { SessionId = sid };

        // expire the access token locally: the next call refreshes transparently, in parallel calls exactly once
        var before = (await store.GetAsync(sid))!;
        await store.UpdateAsync(sid, s => s with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => gateway.GetAsync<AdminDashboardDto>("admin/dashboard", default, options)));
        parallel.ShouldAllBe(r => r.IsSuccess);
        var after = (await store.GetAsync(sid))!;
        after.AccessToken.ShouldNotBe(before.AccessToken);
        after.RefreshToken.ShouldNotBe(before.RefreshToken);
        after.Permissions.ShouldContain(WebPermissions.DashboardAdmin, "the profile is read again on every rotation");
        after.ClientIp.ShouldBe("198.51.100.20", "the sign-in address is kept for later refreshes");

        // someone replays the OLD refresh token (theft): the API revokes the whole family, so our next refresh is refused
        (await _api.PostAsync("/api/v1/auth/refresh", new NexaVerify.Contracts.Identity.RefreshRequest(before.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await store.UpdateAsync(sid, s => s with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        var denied = await gateway.GetAsync<AdminDashboardDto>("admin/dashboard", default, options);
        denied.IsSuccess.ShouldBeFalse();
        denied.Error!.Code.ShouldBe("SESSION_EXPIRED");
        (await store.GetAsync(sid)).ShouldBeNull("reuse detection signs the user out");
    }

    /// <summary>The portal acts for the session of one signed-in user; the typed clients get their gateway through this.</summary>
    private sealed class FixedSessionId(string id) : ICurrentSession
    {
        public Task<string?> GetSessionIdAsync() => Task.FromResult<string?>(id);
    }

    [Fact]
    public async Task A_client_admin_creates_a_key_enrolls_and_identifies_through_the_portal_and_no_credential_reaches_the_browser()
    {
        var platform = await _api.SuperAdminAsync();
        var adminEmail = "ada@pbff-client.test";
        var (client, _) = await _api.OnboardClientAsync(platform.AccessToken, "PBFFC", adminEmail);
        await _api.SeedLicenseAsync(client.Id, 100);
        var browser = Browser();
        var leaks = new List<string>();

        // 1. sign in as the client administrator: lands in the client portal, shell loads, admin-only BFF routes are closed to them
        var login = await SignInAsync(browser, adminEmail, AuthApp.StrongPassword);
        Rel(login.Headers.Location).ShouldBe("/client");
        Collect(leaks, login);
        var shell = await browser.GetAsync("/client");
        shell.StatusCode.ShouldBe(HttpStatusCode.OK);
        Collect(leaks, shell, await shell.Content.ReadAsStringAsync());
        (await browser.GetAsync("/admin")).StatusCode.ShouldNotBe(HttpStatusCode.OK, "client users cannot open the platform console");
        (await browser.GetAsync("/bff/reports/usage.csv")).StatusCode.ShouldNotBe(HttpStatusCode.OK, "the platform report is not for clients");

        var sessionId = SessionIdOf(browser);
        string accessToken, refreshToken, rawKey;
        await using (var scope = _portal.Services.CreateAsyncScope())
        {
            var session = (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId))!;
            accessToken = session.AccessToken;
            refreshToken = session.RefreshToken;
            session.Portal.ShouldBe("client");

            var gateway = new ApiGateway(scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(), new FixedSessionId(sessionId));
            var keys = new ApiKeysApiClient(gateway);
            var faces = new FacesApiClient(gateway);

            // 2. create an API key through the portal's typed client: the raw key is in this one response only
            var scopes = await keys.ScopesAsync();
            scopes.IsSuccess.ShouldBeTrue(scopes.Error?.Message);
            scopes.Value.ShouldAllBe(s => s.Key.StartsWith("faces.", StringComparison.Ordinal), "keys can only carry face recognition scopes");
            var created = await keys.CreateAsync(new CreateApiKeyRequest("Portal test key", ["faces.enroll", "faces.identify", "faces.read"], null, 120, null));
            created.IsSuccess.ShouldBeTrue(created.Error?.Message);
            rawKey = created.Value.RawKey;
            rawKey.ShouldNotBeNullOrWhiteSpace();
            created.Value.Key.Prefix.ShouldNotBe(rawKey, "only a short prefix is stored and listed");

            // 3. the secret cannot be retrieved again, by any read the portal can make
            var list = await gateway.GetAsync<System.Text.Json.JsonElement>("client/api-keys", default, new ApiCallOptions { SessionId = sessionId });
            list.IsSuccess.ShouldBeTrue();
            list.Value.GetRawText().ShouldNotContain(rawKey);
            list.Value.GetRawText().ShouldNotContain("rawKey", Case.Insensitive);
            var one = await gateway.GetAsync<System.Text.Json.JsonElement>($"client/api-keys/{created.Value.Key.Id}", default, new ApiCallOptions { SessionId = sessionId });
            one.Value.GetRawText().ShouldNotContain(rawKey);
            (await keys.ListAsync()).Value.Single().Prefix.ShouldBe(created.Value.Key.Prefix);

            // ... and the key really works for integrators
            var direct = new HttpRequestMessage(HttpMethod.Get, "/api/v1/faces/balance");
            direct.Headers.Add("X-Api-Key", rawKey);
            (await _api.Client.SendAsync(direct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // 4. enroll and identify through the portal: multipart upload, idempotency key, credits charged, answer matches
            var enrollKey = PhotoGuard.NewIdempotencyKey();
            var enrolled = await faces.EnrollAsync(
                new EnrollFaceRequest("EMP-7", "Eve Example", null, "FORM-7"), new CapturedImage(TestImages.Person(7), "image/jpeg", "eve.jpg"), enrollKey);
            enrolled.IsSuccess.ShouldBeTrue(enrolled.Error?.Message);
            enrolled.Value.ProfileCreated.ShouldBeTrue();
            enrolled.Value.Credits.Charged.ShouldBeGreaterThan(0);
            var replay = await faces.EnrollAsync(
                new EnrollFaceRequest("EMP-7", "Eve Example", null, "FORM-7"), new CapturedImage(TestImages.Person(7), "image/jpeg", "eve.jpg"), enrollKey);
            replay.Value.RequestId.ShouldBe(enrolled.Value.RequestId, "the same idempotency key is never charged twice");

            var identified = await faces.IdentifyAsync(new IdentifyFaceRequest(3), new CapturedImage(TestImages.Person(7, variation: 3), "image/jpeg", "visitor.jpg"), PhotoGuard.NewIdempotencyKey());
            identified.IsSuccess.ShouldBeTrue(identified.Error?.Message);
            identified.Value.Matches.Single().ExternalRef.ShouldBe("EMP-7");
            var stranger = await faces.IdentifyAsync(new IdentifyFaceRequest(3), new CapturedImage(TestImages.Person(99), "image/jpeg", "stranger.jpg"), PhotoGuard.NewIdempotencyKey());
            stranger.Value.Matches.ShouldBeEmpty();

            // an oversized or non-image upload never gets past the API either
            var notImage = await faces.VerifyAsync(new VerifyFaceRequest(null, "EMP-7"), new CapturedImage("hello"u8.ToArray(), "image/jpeg", "x.jpg"), PhotoGuard.NewIdempotencyKey());
            notImage.IsSuccess.ShouldBeFalse();

            // history and balance reflect what happened
            var history = await faces.GetHistoryAsync(new RecognitionHistoryQuery());
            history.Value.Items.Select(i => i.Operation).ShouldContain("Identify");
            var balance = await new ClientLicenseApiClient(gateway).GetSummaryAsync();
            balance.Value.RemainingCredits.ShouldBeLessThan(100);

            // keys can be revoked from the portal as well
            (await keys.RevokeAsync(created.Value.Key.Id, "end of test")).IsSuccess.ShouldBeTrue();
            var revoked = new HttpRequestMessage(HttpMethod.Get, "/api/v1/faces/balance");
            revoked.Headers.Add("X-Api-Key", rawKey);
            (await _api.Client.SendAsync(revoked)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // 5. the client's own usage CSV is relayed through the BFF (headers right, bad dates refused, anonymous visitors turned away)
        var csv = await browser.GetAsync("/bff/client/reports/usage.csv?from=2026-01-01&to=2026-01-31");
        csv.StatusCode.ShouldBe(HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        csv.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        Collect(leaks, csv, await csv.Content.ReadAsStringAsync());
        (await browser.GetAsync("/bff/client/reports/usage.csv?from=nope")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser().GetAsync("/bff/client/reports/usage.csv")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // 6. sign out; the session is gone
        var signedOut = await browser.GetAsync("/auth/signed-out");
        signedOut.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Collect(leaks, signedOut);
        await using (var scope = _portal.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId)).ShouldBeNull();
        }

        (await browser.GetAsync("/bff/client/reports/usage.csv")).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // nothing the browser received contained a token, the one-time API key or any JWT
        foreach (var seen in leaks)
        {
            seen.ShouldNotContain(accessToken);
            seen.ShouldNotContain(refreshToken);
            seen.ShouldNotContain(rawKey);
            seen.ShouldNotContain("eyJ");
            seen.ShouldNotContain("nxv_live_");
        }
    }

    [Fact]
    public async Task A_website_cannot_sign_a_visitor_out_but_the_portal_itself_can()
    {
        await _api.SuperAdminAsync();
        var browser = Browser();
        (await SignInAsync(browser, DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var sessionId = SessionIdOf(browser);

        browser.FetchSite = "cross-site";
        var attack = await browser.GetAsync("/auth/signed-out");

        attack.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        attack.Headers.Contains("Set-Cookie").ShouldBeFalse("the victim's cookie must not be cleared");
        await using (var scope = _portal.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId)).ShouldNotBeNull();
        }

        browser.FetchSite = "same-origin";
        (await browser.GetAsync("/auth/signed-out")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await using (var scope = _portal.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ISessionStore>().GetAsync(sessionId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task One_noisy_visitor_cannot_use_up_the_credential_limit_of_everybody_else()
    {
        // The API trusts the portal as a proxy and limits sign-in / refresh / password-reset calls to 4 per minute per person.
        var api = await AuthApp.CreateAsync(_fixture,
            new Dictionary<string, string>
            {
                ["ForwardedHeaders:Enabled"] = "true",
                ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
                ["RateLimiting:AuthPerIpPerMinute"] = "4",
            },
            services => services.AddTransient<IStartupFilter, FakeRemoteIpStartupFilter>());
        await using var _ = api;
        await api.SuperAdminAsync(); // direct calls from 127.0.0.1: their own bucket, not the people's
        await using var portal = PortalFor(api);

        // Person C signs in and keeps a session.
        await using var sessionScope = portal.Services.CreateAsyncScope();
        var signIn = await sessionScope.ServiceProvider.GetRequiredService<IPortalAuth>()
            .SignInAsync(DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword, default, "198.51.100.30");
        signIn.IsSuccess.ShouldBeTrue(signIn.Error?.Message);

        // Visitor A hammers "forgot password" from their own circuit.
        var results = new List<ApiResult<bool>>();
        await using (var attacker = portal.Services.CreateAsyncScope())
        {
            attacker.ServiceProvider.GetRequiredService<ClientAddress>().Value = "203.0.113.1";
            var forgot = attacker.ServiceProvider.GetRequiredService<IAuthApiClient>();
            for (var i = 0; i < 10; i++)
            {
                results.Add(await forgot.ForgotPasswordAsync(new ForgotPasswordModel { Email = "nobody@nowhere.test" }));
            }
        }

        results.Count(r => !r.IsSuccess && r.Error!.Status == 429).ShouldBeGreaterThan(0, "the noisy visitor is limited");

        // Visitor B (another circuit) is not affected ...
        await using (var bystander = portal.Services.CreateAsyncScope())
        {
            bystander.ServiceProvider.GetRequiredService<ClientAddress>().Value = "203.0.113.2";
            var ok = await bystander.ServiceProvider.GetRequiredService<IAuthApiClient>().ForgotPasswordAsync(new ForgotPasswordModel { Email = "nobody@nowhere.test" });
            ok.IsSuccess.ShouldBeTrue(ok.Error?.Message);
        }

        // ... and neither is person C's token refresh.
        var store = sessionScope.ServiceProvider.GetRequiredService<ISessionStore>();
        await store.UpdateAsync(signIn.Value.Id, s => s with { AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        var dashboard = await sessionScope.ServiceProvider.GetRequiredService<IApiGateway>()
            .GetAsync<AdminDashboardDto>("admin/dashboard", default, new ApiCallOptions { SessionId = signIn.Value.Id });
        dashboard.IsSuccess.ShouldBeTrue(dashboard.Error?.Message);
        (await store.GetAsync(signIn.Value.Id))!.RefreshToken.ShouldNotBe(signIn.Value.RefreshToken, "the refresh really happened");
    }

    private static void Collect(List<string> leaks, HttpResponseMessage response, string? body = null)
    {
        leaks.Add(response.ToString());
        if (body is not null)
        {
            leaks.Add(body);
        }
    }
}
