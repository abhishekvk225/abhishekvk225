using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Components;
using NexaVerify.Web.Security;
using LoginMfaPage = NexaVerify.Web.Pages.LoginMfa;
using MfaEnrollPage = NexaVerify.Web.Pages.MfaEnroll;

namespace NexaVerify.Web.ComponentTests;

/// <summary>The second sign-in step (/login/mfa) and the enrolment page (/mfa/enroll).</summary>
public class MfaPagesTests : PageTestBase
{
    private const string OtpUri = "otpauth://totp/NexaVerify:ada%40nexaverify.test?secret=JBSWY3DPEHPK3PXP&issuer=NexaVerify&algorithm=SHA1&digits=6&period=30";

    private sealed class FakeMfaAuth : IPortalAuth
    {
        public List<string> Calls { get; } = [];

        public ApiResult<MfaEnrolmentDto> Begin { get; set; } = ApiResult<MfaEnrolmentDto>.Ok(new MfaEnrolmentDto("JBSWY3DPEHPK3PXP", OtpUri, "NexaVerify", "ada@nexaverify.test", "SHA1", 6, 30));

        public ApiResult<IReadOnlyList<string>> Confirm { get; set; } = ApiResult<IReadOnlyList<string>>.Ok(["ABCDE-FGHJK", "LMNPQ-RSTUV"]);

        public Task<ApiResult<MfaEnrolmentDto>> BeginMfaEnrolmentAsync(string sessionId, CancellationToken ct = default)
        {
            Calls.Add("begin:" + sessionId);
            return Task.FromResult(Begin);
        }

        public Task<ApiResult<IReadOnlyList<string>>> ConfirmMfaEnrolmentAsync(string sessionId, string code, CancellationToken ct = default)
        {
            Calls.Add("confirm:" + code);
            return Task.FromResult(Confirm);
        }

        public Task<ApiResult<SignInStep>> BeginSignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null) => throw new NotSupportedException();

        public Task<ApiResult<PortalSession>> SignInAsync(string email, string password, CancellationToken ct = default, string? clientIp = null) => throw new NotSupportedException();

        public Task<ApiResult<PortalSession>> CompleteMfaSignInAsync(MfaPending pending, string code, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ApiResult<bool>> ChangePasswordAsync(string sessionId, ChangePasswordModel model, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SignOutAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- /login/mfa ----

    private IRenderedComponent<LoginMfaPage> RenderSecondStep(string? error = null)
    {
        Services.TryAddSingleton(Options.Create(new CookieSecurityOptions()));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login/mfa" + (error is null ? string.Empty : "?error=" + Uri.EscapeDataString(error)));
        return Render<LoginMfaPage>();
    }

    [Fact]
    public void The_second_step_is_a_static_form_that_posts_to_the_bff_with_an_antiforgery_token()
    {
        var cut = RenderSecondStep();

        var form = cut.Find("form[data-testid=mfa-form]");
        form.GetAttribute("method").ShouldBe("post");
        form.GetAttribute("action").ShouldBe("/auth/mfa");
        cut.Find("input[name=__RequestVerificationToken]").GetAttribute("value").ShouldBe("form-token-123");
        cut.Find("label[for=mfa-code]").TextContent.ShouldBe("Verification code");
        var input = cut.Find("#mfa-code");
        input.GetAttribute("name").ShouldBe("code");
        input.GetAttribute("autocomplete").ShouldBe("one-time-code");
        input.GetAttribute("maxlength").ShouldBe("32");
        cut.Find("[data-testid=mfa-back]").GetAttribute("href").ShouldBe("/login");
        cut.FindAll("[data-testid=mfa-error]").ShouldBeEmpty();
        cut.Markup.ShouldContain("recovery code");
    }

    [Theory]
    [InlineData("mfa-invalid", "That code is not right.")]
    [InlineData("throttled", "Too many sign-in attempts.")]
    [InlineData("unavailable", "We couldn't sign you in right now.")]
    public void Failures_are_explained_without_saying_which_part_was_wrong(string code, string message)
    {
        var cut = RenderSecondStep(code);

        var alert = cut.Find("[data-testid=mfa-error]");
        alert.GetAttribute("role").ShouldBe("alert");
        alert.TextContent.ShouldContain(message);
    }

    [Fact]
    public void Unknown_error_codes_are_ignored_and_never_echoed()
    {
        var cut = RenderSecondStep("<script>alert(1)</script>");

        cut.FindAll("[data-testid=mfa-error]").ShouldBeEmpty();
        cut.Markup.ShouldNotContain("<script>alert");
    }

    [Fact]
    public void The_challenge_token_never_appears_anywhere_on_the_page()
    {
        // The page cannot even know the token: it is parked on the server and named by an HttpOnly cookie.
        var cut = RenderSecondStep();

        cut.Markup.ShouldNotContain("challenge", Case.Insensitive);
        cut.FindAll("input[type=hidden]").Select(i => i.GetAttribute("name")).ShouldBe(["__RequestVerificationToken"]);
    }

    // ---- /mfa/enroll ----

    private IRenderedComponent<MudDialogProvider> _dialogs = null!;

    private (IRenderedComponent<MfaEnrollPage> Cut, FakeMfaAuth Auth, Bunit.TestDoubles.BunitNavigationManager Nav) RenderEnroll(bool required = true, FakeMfaAuth? auth = null)
    {
        var session = SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: PortalKinds.Admin) with { MfaEnrolmentRequired = required };
        Services.AddSingleton<AuthenticationStateProvider>(FakeAuthState.For(session));
        auth ??= new FakeMfaAuth();
        Services.AddSingleton<IPortalAuth>(auth);
        Services.AddSingleton<ICurrentSession>(new FixedSession(session.Id));
        _dialogs = Providers();
        var cut = Render<CascadingAuthenticationState>(p => p.AddChildContent<MfaEnrollPage>()).FindComponent<MfaEnrollPage>();
        return (cut, auth, (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<NavigationManager>());
    }

    [Fact]
    public void Enrolment_starts_once_and_shows_a_qr_code_the_key_and_the_setup_link()
    {
        var (cut, auth, _) = RenderEnroll();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));
        auth.Calls.ShouldBe(["begin:sid-1"]);
        var svg = cut.Find("[data-testid=qr-code]");
        svg.TagName.ToLowerInvariant().ShouldBe("svg");
        svg.GetAttribute("role").ShouldBe("img");
        svg.GetAttribute("aria-label")!.ShouldContain("QR code");
        svg.QuerySelector("path")!.GetAttribute("d")!.ShouldStartWith("M");
        cut.Find("[data-testid=mfa-secret]").TextContent.ShouldBe("JBSW Y3DP EHPK 3PXP");
        cut.Find("[data-testid=mfa-uri]").TextContent.ShouldBe(OtpUri);
        cut.Markup.ShouldContain("needs a second sign-in step");
        cut.Markup.ShouldNotContain("<script");
    }

    [Fact]
    public void A_voluntary_visit_explains_it_differently_from_a_required_one()
    {
        var (cut, _, _) = RenderEnroll(required: false);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));
        cut.Markup.ShouldNotContain("needs a second sign-in step");
        cut.Markup.ShouldContain("Add a second sign-in step");
    }

    [Fact]
    public void A_start_failure_is_shown_with_its_reference_and_can_be_retried()
    {
        var auth = new FakeMfaAuth { Begin = ApiResult<MfaEnrolmentDto>.Fail("CONFLICT", "Two-factor authentication is already enabled.", "corr-mfa", 409) };
        var (cut, _, _) = RenderEnroll(auth: auth);

        cut.WaitForAssertion(() => cut.Find("[data-testid=mfa-enroll-error]").TextContent.ShouldContain("already enabled"));
        cut.Find("[data-testid=mfa-enroll-error]").TextContent.ShouldContain("corr-mfa");
        cut.FindAll("[data-testid=qr-code]").ShouldBeEmpty();

        auth.Begin = ApiResult<MfaEnrolmentDto>.Ok(new MfaEnrolmentDto("JBSWY3DPEHPK3PXP", OtpUri, "NexaVerify", "ada@nexaverify.test", "SHA1", 6, 30));
        cut.Find("[data-testid=mfa-retry]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));
        auth.Calls.Count(c => c.StartsWith("begin", StringComparison.Ordinal)).ShouldBe(2);
    }

    private static void Type(IRenderedComponent<MfaEnrollPage> cut, string value) =>
        cut.FindComponents<MudTextField<string>>().Single().Find("input").Input(value);

    [Fact]
    public void The_code_is_checked_before_the_api_is_called()
    {
        var (cut, auth, _) = RenderEnroll();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));

        Type(cut, "12");
        cut.Find("[data-testid=mfa-confirm]").Click();

        cut.Markup.ShouldContain("Enter the 6-digit code from your authenticator app.");
        auth.Calls.ShouldNotContain(c => c.StartsWith("confirm", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrong_code_is_reported_and_the_setup_stays_on_screen()
    {
        var auth = new FakeMfaAuth { Confirm = ApiResult<IReadOnlyList<string>>.Fail("VALIDATION_FAILED", "The verification code is not valid.", "corr-c", 400) };
        var (cut, _, _) = RenderEnroll(auth: auth);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));

        Type(cut, "123456");
        cut.Find("[data-testid=mfa-confirm]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=mfa-enroll-error]").TextContent.ShouldContain("verification code is not valid"));
        cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1);
    }

    [Fact]
    public void An_exhausted_setup_asks_to_start_again_with_a_new_secret()
    {
        var auth = new FakeMfaAuth { Confirm = ApiResult<IReadOnlyList<string>>.Fail("CONFLICT", "There is no pending two-factor setup. Start the setup again.", "corr-x", 409) };
        var (cut, _, _) = RenderEnroll(auth: auth);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));

        Type(cut, "123456");
        cut.Find("[data-testid=mfa-confirm]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mfa-retry]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=qr-code]").ShouldBeEmpty();
    }

    [Fact]
    public void Recovery_codes_are_shown_once_in_the_reveal_dialog_then_the_portal_reloads()
    {
        var (cut, auth, nav) = RenderEnroll();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=qr-code]").Count.ShouldBe(1));
        var provider = _dialogs;

        Type(cut, "123456");
        cut.Find("[data-testid=mfa-confirm]").Click();

        provider.WaitForAssertion(() => provider.FindAll("[data-testid=secret-value]").Count.ShouldBe(1));
        auth.Calls.ShouldContain("confirm:123456");
        var shown = provider.Find("[data-testid=secret-value]").TextContent;
        shown.ShouldContain("ABCDE-FGHJK");
        shown.ShouldContain("LMNPQ-RSTUV");
        cut.FindAll("[data-testid=qr-code]").ShouldBeEmpty("the setup secret is no longer rendered once MFA is on");
        cut.Markup.ShouldNotContain("JBSW Y3DP");

        provider.Find(".mud-dialog input[type=checkbox]").Change(true);
        provider.Find("[data-testid=secret-done]").Click();

        cut.WaitForAssertion(() => nav.History.First().Uri.ShouldBe("/admin"));
        nav.History.First().Options.ForceLoad.ShouldBeTrue();
        provider.Markup.ShouldNotContain("ABCDE-FGHJK");
    }

    // ---- the QR code itself ----

    [Fact]
    public void The_qr_code_is_drawn_from_modules_with_a_finder_pattern_and_is_deterministic()
    {
        var (size, path) = QrMatrix.Draw(OtpUri);

        size.ShouldBeGreaterThan(29);
        path.ShouldStartWith("M");
        System.Text.RegularExpressions.Regex.IsMatch(path, @"^(M\d+,\d+h1v1h-1z)+$").ShouldBeTrue("only square modules are drawn: nothing a browser could execute");
        QrMatrix.Draw(OtpUri).ShouldBe((size, path));
        QrMatrix.Draw(OtpUri + "x").Path.ShouldNotBe(path);
        // the top-left finder pattern is a solid 7x7 ring with the quiet zone around it: its first row has seven dark modules in a row
        var firstDark = System.Text.RegularExpressions.Regex.Match(path, @"M(\d+),(\d+)h1v1h-1z");
        var (x0, y0) = (int.Parse(firstDark.Groups[1].Value), int.Parse(firstDark.Groups[2].Value));
        for (var dx = 0; dx < 7; dx++)
        {
            path.ShouldContain($"M{x0 + dx},{y0}h1v1h-1z");
        }

        QrMatrix.Draw(string.Empty).ShouldBe((0, string.Empty));
    }

    [Fact]
    public void The_qr_component_renders_an_inline_svg_sized_as_asked()
    {
        var cut = Render<QrSvg>(p => p.Add(c => c.Value, OtpUri).Add(c => c.Pixels, 160).Add(c => c.Label, "Scan me"));

        var svg = cut.Find("svg");
        svg.GetAttribute("width").ShouldBe("160");
        svg.GetAttribute("aria-label").ShouldBe("Scan me");
        svg.GetAttribute("viewBox").ShouldStartWith("0 0 ");
        cut.FindAll("rect").Count.ShouldBe(1);
        cut.FindAll("path").Count.ShouldBe(1);
    }

    // ---- the pending store ----

    private static (DistributedMfaPendingStore Store, SessionFixtures.DictionaryCache Cache, Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock) NewPendingStore()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start);
        var cache = new SessionFixtures.DictionaryCache();
        return (new DistributedMfaPendingStore(cache, new EphemeralDataProtectionProvider(), clock), cache, clock);
    }

    [Fact]
    public async Task A_parked_challenge_is_encrypted_expires_and_can_be_removed()
    {
        var (store, cache, clock) = NewPendingStore();
        var pending = new MfaPending("challenge-secret-value", "ada@nexaverify.test", "203.0.113.5", "/admin/clients", clock.GetUtcNow().AddMinutes(5));

        var id = await store.CreateAsync(pending);

        id.Length.ShouldBeGreaterThanOrEqualTo(43);
        var stored = cache.Get("nv:mfa-pending:" + id)!;
        System.Text.Encoding.UTF8.GetString(stored).ShouldNotContain("challenge-secret-value");
        (await store.GetAsync(id)).ShouldBe(pending);
        pending.ToString().ShouldNotContain("challenge-secret-value");

        clock.Advance(TimeSpan.FromMinutes(5));
        (await store.GetAsync(id)).ShouldBeNull();

        var again = await store.CreateAsync(pending with { ExpiresAt = clock.GetUtcNow().AddMinutes(5) });
        await store.RemoveAsync(again);
        (await store.GetAsync(again)).ShouldBeNull();
        (await store.GetAsync(null)).ShouldBeNull();
        (await store.GetAsync(new string('x', 500))).ShouldBeNull();
        (await store.GetAsync("never-issued")).ShouldBeNull();
    }

    // ---- the portal auth service: password step, second step, enrolment ----

    private static (PortalAuth Auth, FakeAuthApi Api, BffHarness H) BuildAuth()
    {
        var h = new BffHarness();
        var api = new FakeAuthApi();
        return (new PortalAuth(api, h.Store, h.Coordinator, h.Clock, Options.Create(h.Options), NullLogger<PortalAuth>.Instance), api, h);
    }

    private static ApiResult<LoginResponse> Challenge(int expiresIn = 300) =>
        ApiResult<LoginResponse>.Ok(new LoginResponse(string.Empty, "Bearer", 0, string.Empty, false,
            new UserSummary(Guid.NewGuid(), "ada@nexaverify.test", "Ada Admin", true, null, []), MfaRequired: true, MfaChallengeToken: "challenge-xyz", MfaChallengeExpiresIn: expiresIn));

    [Fact]
    public async Task A_password_step_that_needs_a_second_factor_creates_no_session_and_parks_the_challenge()
    {
        var (auth, api, h) = BuildAuth();
        api.Login = Challenge();

        var result = await auth.BeginSignInAsync(" ada@nexaverify.test ", "pw", default, "203.0.113.5");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Session.ShouldBeNull();
        var pending = result.Value.Pending!;
        pending.ChallengeToken.ShouldBe("challenge-xyz");
        pending.Email.ShouldBe("ada@nexaverify.test");
        pending.ClientIp.ShouldBe("203.0.113.5");
        pending.ExpiresAt.ShouldBe(h.Clock.GetUtcNow().AddMinutes(5));
        api.Calls.ShouldBe(["login"]); // no profile call: there is no token to ask with
    }

    [Fact]
    public async Task The_simple_sign_in_refuses_an_account_that_needs_a_second_factor()
    {
        var (auth, api, _) = BuildAuth();
        api.Login = Challenge();

        var result = await auth.SignInAsync("ada@nexaverify.test", "pw");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(PortalAuth.MfaRequiredCode);
    }

    [Fact]
    public async Task Completing_the_second_step_creates_the_session_from_the_verified_tokens()
    {
        var (auth, api, h) = BuildAuth();
        var pending = new MfaPending("challenge-xyz", "ada@nexaverify.test", "203.0.113.5", null, h.Clock.GetUtcNow().AddMinutes(5));

        var result = await auth.CompleteMfaSignInAsync(pending, " 123456 ");

        result.IsSuccess.ShouldBeTrue();
        api.LastVerify.ShouldBe(new VerifyMfaRequest("challenge-xyz", "123456"));
        api.LastClientIp.ShouldBe("203.0.113.5"); // the person's address is what the API sees, not the portal's
        var session = (await h.Store.GetAsync(result.Value.Id))!;
        session.AccessToken.ShouldBe("access-mfa");
        session.RefreshToken.ShouldBe("refresh-mfa");
        api.Calls.ShouldBe(["verify-mfa", "me:access-mfa"]);
    }

    [Fact]
    public async Task A_refused_code_creates_no_session_and_keeps_the_apis_reason()
    {
        var (auth, api, h) = BuildAuth();
        api.Verify = ApiResult<LoginResponse>.Fail("MFA_CODE_INVALID", "The verification code is not valid.", "corr-v", 401);

        var result = await auth.CompleteMfaSignInAsync(new MfaPending("c", "e", null, null, h.Clock.GetUtcNow().AddMinutes(5)), "000000");

        result.IsSuccess.ShouldBeFalse();
        (result.Error!.Code, result.Error.Status).ShouldBe(("MFA_CODE_INVALID", 401));
        api.Calls.ShouldBe(["verify-mfa"]);
    }

    [Fact]
    public async Task A_token_that_only_allows_enrolment_marks_the_session_until_enrolment_succeeds()
    {
        var (auth, api, h) = BuildAuth();
        api.Login = ApiResult<LoginResponse>.Ok(new LoginResponse("access-1", "Bearer", 900, "refresh-1", false,
            new UserSummary(Guid.NewGuid(), "ada@nexaverify.test", "Ada Admin", true, null, ["SuperAdmin"]), MfaEnrolmentRequired: true));

        var signedIn = (await auth.SignInAsync("ada@nexaverify.test", "pw")).Value;
        (await h.Store.GetAsync(signedIn.Id))!.MfaEnrolmentRequired.ShouldBeTrue();

        var codes = await auth.ConfirmMfaEnrolmentAsync(signedIn.Id, " 123456 ");

        codes.IsSuccess.ShouldBeTrue();
        codes.Value.ShouldBe(["ABCDE-FGHJK", "LMNPQ-RSTUV"]);
        api.Calls.ShouldContain("mfa-confirm:123456");
        var after = (await h.Store.GetAsync(signedIn.Id))!;
        after.MfaEnrolmentRequired.ShouldBeFalse();
        after.AccessToken.ShouldBe("access-enabled"); // the pair issued when MFA was turned on replaces the ended one
        after.RefreshToken.ShouldBe("refresh-enabled");
    }

    [Fact]
    public async Task A_failed_confirmation_leaves_the_session_untouched()
    {
        var (auth, api, h) = BuildAuth();
        var signedIn = (await auth.SignInAsync("ada@nexaverify.test", "pw")).Value;
        api.Enabled = ApiResult<MfaEnabledDto>.Fail("VALIDATION_FAILED", "The verification code is not valid.", null, 400);

        (await auth.ConfirmMfaEnrolmentAsync(signedIn.Id, "000000")).IsSuccess.ShouldBeFalse();

        (await h.Store.GetAsync(signedIn.Id))!.AccessToken.ShouldBe("access-1");
    }

    // ---- authorization while enrolment is pending ----

    [Fact]
    public void An_enrolment_pending_principal_holds_no_portal_permissions_but_may_reach_the_enrolment_page()
    {
        var session = SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: PortalKinds.Admin) with { MfaEnrolmentRequired = true };
        var principal = PortalPrincipalFactory.Create(session);
        var handler = new PortalRequirementHandler();

        bool Allowed(PortalRequirement requirement)
        {
            var context = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext([requirement], principal, null);
            handler.HandleAsync(context).GetAwaiter().GetResult();
            return context.HasSucceeded;
        }

        principal.HasClaim(PortalClaims.MfaEnrolmentRequired, "true").ShouldBeTrue();
        Allowed(new PortalRequirement(PortalKinds.Admin, null, false)).ShouldBeFalse();
        Allowed(new PortalRequirement(null, "dashboard.admin", false)).ShouldBeFalse();
        Allowed(new PortalRequirement(null, null, true)).ShouldBeTrue(); // the SignedIn policy: the enrolment page
        PortalPrincipalFactory.SameAuthorization(principal, session).ShouldBeTrue();
        PortalPrincipalFactory.SameAuthorization(principal, session with { MfaEnrolmentRequired = false }).ShouldBeFalse("a circuit built before enrolment must notice it ended");
    }

    [Fact]
    public async Task The_middleware_sends_page_requests_to_the_enrolment_page_but_lets_its_own_routes_through()
    {
        var session = SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: PortalKinds.Admin) with { MfaEnrolmentRequired = true };
        var middleware = new ForcePasswordChangeMiddleware(_ => Task.CompletedTask);

        async Task<(string? Location, int Status)> Visit(string path, string method = "GET", PortalSession? as_ = null)
        {
            var context = new DefaultHttpContext { User = PortalPrincipalFactory.Create(as_ ?? session) };
            context.Request.Method = method;
            context.Request.Path = path;
            await middleware.InvokeAsync(context);
            return (context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null, context.Response.StatusCode);
        }

        (await Visit("/admin")).Location.ShouldBe("/mfa/enroll");
        (await Visit("/admin/clients")).Location.ShouldBe("/mfa/enroll");
        (await Visit("/mfa/enroll")).Location.ShouldBeNull();
        (await Visit("/auth/signed-out")).Location.ShouldBeNull();
        (await Visit("/_blazor")).Location.ShouldBeNull();
        (await Visit("/admin", "POST")).Location.ShouldBeNull();
        (await Visit("/admin", as_: session with { MfaEnrolmentRequired = false })).Location.ShouldBeNull();
        // a pending password change comes first
        (await Visit("/admin", as_: session with { MustChangePassword = true })).Location.ShouldBe("/change-password");
    }
}
