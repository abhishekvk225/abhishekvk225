using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public sealed class FakeAuthApi : IAuthApiClient
{
    public List<string> Calls { get; } = [];

    public string? LastClientIp { get; private set; }

    public ApiResult<LoginResponse> Login { get; set; } = FakeExchange.Tokens("access-1", "refresh-1");

    public ApiResult<MeResponse> Me { get; set; } = ApiResult<MeResponse>.Ok(new MeResponse(
        new UserSummary(Guid.NewGuid(), "ada@nexaverify.test", "Ada Admin", true, null, ["SuperAdmin"]), ["dashboard.admin", "clients.read"], false, null));

    public ApiResult<LoginResponse> Change { get; set; } = FakeExchange.Tokens("access-9", "refresh-9");

    public Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default, string? clientIp = null)
    {
        Calls.Add("login");
        LastClientIp = clientIp;
        return Task.FromResult(Login);
    }

    public Task<ApiResult<MeResponse>> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        Calls.Add("me:" + accessToken);
        return Task.FromResult(Me);
    }

    public Task<ApiResult<bool>> LogoutAsync(string accessToken, string refreshToken, CancellationToken ct = default)
    {
        Calls.Add($"logout:{accessToken}:{refreshToken}");
        return Task.FromResult(ApiResult<bool>.Ok(true));
    }

    public Task<ApiResult<bool>> ForgotPasswordAsync(ForgotPasswordModel model, CancellationToken ct = default) => Task.FromResult(ApiResult<bool>.Ok(true));

    public Task<ApiResult<bool>> ResetPasswordAsync(ResetPasswordModel model, CancellationToken ct = default) => Task.FromResult(ApiResult<bool>.Ok(true));

    public Task<ApiResult<LoginResponse>> ChangePasswordAsync(string sessionId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        Calls.Add("change:" + sessionId);
        return Task.FromResult(Change);
    }
}

public class PortalAuthTests
{
    private static (PortalAuth Auth, FakeAuthApi Api, BffHarness H) Build()
    {
        var h = new BffHarness();
        var api = new FakeAuthApi();
        var auth = new PortalAuth(api, h.Store, h.Coordinator, h.Clock, Options.Create(h.Options), NullLogger<PortalAuth>.Instance);
        return (auth, api, h);
    }

    [Fact]
    public async Task Signing_in_creates_a_server_side_session_with_the_permissions_from_the_profile()
    {
        var (auth, api, h) = Build();

        var result = await auth.SignInAsync(" ada@nexaverify.test ", "pw");

        result.IsSuccess.ShouldBeTrue();
        var session = (await h.Store.GetAsync(result.Value.Id))!;
        session.Portal.ShouldBe(PortalKinds.Admin);
        session.AccessToken.ShouldBe("access-1");
        session.RefreshToken.ShouldBe("refresh-1");
        session.Permissions.ShouldContain("dashboard.admin");
        session.AbsoluteExpiresAt.ShouldBe(h.Clock.GetUtcNow().AddHours(12));
        session.AccessTokenExpiresAt.ShouldBe(h.Clock.GetUtcNow().AddSeconds(900));
        api.Calls.ShouldBe(["login", "me:access-1"]);
    }

    [Fact]
    public async Task The_end_users_address_is_passed_on_so_the_api_can_rate_limit_the_person_not_the_portal()
    {
        var (auth, api, _) = Build();

        await auth.SignInAsync("a@b.test", "pw", default, "203.0.113.9");

        api.LastClientIp.ShouldBe("203.0.113.9");
    }

    [Fact]
    public async Task Every_sign_in_gets_a_fresh_unguessable_session_id()
    {
        var (auth, _, _) = Build();

        var first = await auth.SignInAsync("a@b.test", "pw");
        var second = await auth.SignInAsync("a@b.test", "pw");

        first.Value.Id.ShouldNotBe(second.Value.Id);
    }

    [Fact]
    public async Task Client_users_land_in_the_client_portal()
    {
        var (auth, api, h) = Build();
        var clientId = Guid.NewGuid();
        api.Me = ApiResult<MeResponse>.Ok(new MeResponse(
            new UserSummary(Guid.NewGuid(), "u@acme.test", "Una User", false, clientId, ["ClientAdmin"]), ["dashboard.client"], false,
            new ClientSummary(clientId, "ACME", "Acme Corp", "Active", "UTC")));

        var session = (await auth.SignInAsync("u@acme.test", "pw")).Value;

        session.Portal.ShouldBe(PortalKinds.Client);
        session.ClientName.ShouldBe("Acme Corp");
        session.ClientId.ShouldBe(clientId);
        (await h.Store.GetAsync(session.Id))!.Permissions.ShouldBe(["dashboard.client"]);
    }

    [Fact]
    public async Task A_pending_password_change_is_remembered_from_either_response()
    {
        var (auth, api, _) = Build();
        api.Login = FakeExchange.Tokens("a", "r", mustChange: true);

        (await auth.SignInAsync("a@b.test", "pw")).Value.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_logins_create_nothing_and_pass_the_api_error_through()
    {
        var (auth, api, _) = Build();
        api.Login = ApiResult<LoginResponse>.Fail("UNAUTHENTICATED", "The email or password is not correct.", "c-1", 401);

        var result = await auth.SignInAsync("a@b.test", "bad");

        result.Error!.Status.ShouldBe(401);
        api.Calls.ShouldBe(["login"]);
    }

    [Fact]
    public async Task If_the_profile_cannot_be_loaded_the_fresh_tokens_are_revoked_and_no_session_is_left_behind()
    {
        var (auth, api, _) = Build();
        api.Me = ApiResult<MeResponse>.Fail("INTERNAL_ERROR", "down", null, 503);

        var result = await auth.SignInAsync("a@b.test", "pw");

        result.IsSuccess.ShouldBeFalse();
        api.Calls.ShouldContain("logout:access-1:refresh-1");
    }

    [Fact]
    public async Task Changing_the_password_swaps_in_the_new_tokens_and_clears_the_forced_change_flag()
    {
        var (auth, api, h) = Build();
        api.Login = FakeExchange.Tokens("access-1", "refresh-1", mustChange: true);
        var sid = (await auth.SignInAsync("a@b.test", "pw")).Value.Id;

        var result = await auth.ChangePasswordAsync(sid, new ChangePasswordModel { CurrentPassword = "old", NewPassword = "new-password-123", ConfirmPassword = "new-password-123" });

        result.IsSuccess.ShouldBeTrue();
        var session = (await h.Store.GetAsync(sid))!;
        session.AccessToken.ShouldBe("access-9");
        session.RefreshToken.ShouldBe("refresh-9");
        session.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task A_rejected_password_change_keeps_the_session_as_it_was()
    {
        var (auth, api, h) = Build();
        var sid = (await auth.SignInAsync("a@b.test", "pw")).Value.Id;
        api.Change = ApiResult<LoginResponse>.Fail("VALIDATION_FAILED", "The current password is incorrect.", "c-2", 400);

        var result = await auth.ChangePasswordAsync(sid, new ChangePasswordModel { CurrentPassword = "x", NewPassword = "y", ConfirmPassword = "y" });

        result.IsSuccess.ShouldBeFalse();
        (await h.Store.GetAsync(sid))!.AccessToken.ShouldBe("access-1");
    }

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token_at_the_api_and_destroys_the_session()
    {
        var (auth, api, h) = Build();
        var sid = (await auth.SignInAsync("a@b.test", "pw")).Value.Id;

        await auth.SignOutAsync(sid);

        api.Calls.ShouldContain("logout:access-1:refresh-1");
        (await h.Store.GetAsync(sid)).ShouldBeNull();
    }

    [Fact]
    public async Task Signing_out_with_an_expired_access_token_refreshes_first_so_revocation_still_works()
    {
        var (auth, api, h) = Build();
        var sid = (await auth.SignInAsync("a@b.test", "pw")).Value.Id;
        await h.Store.UpdateAsync(sid, s => s with { AccessTokenExpiresAt = h.Clock.GetUtcNow().AddMinutes(-1) });

        await auth.SignOutAsync(sid);

        h.Exchange.Calls.ShouldBe(1);
        api.Calls.ShouldContain("logout:access-2:refresh-2");
        (await h.Store.GetAsync(sid)).ShouldBeNull();
    }

    [Fact]
    public async Task Signing_out_twice_or_with_an_unknown_id_is_harmless()
    {
        var (auth, _, _) = Build();

        await auth.SignOutAsync("never-existed");
        await auth.SignOutAsync("never-existed");
    }
}

public class PortalPrincipalAndPolicyTests
{
    private static PortalSession Session(string portal = PortalKinds.Admin, bool mustChange = false, params string[] permissions) =>
        SessionFixtures.NewSession(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start), portal: portal) with
        {
            MustChangePassword = mustChange,
            Permissions = permissions,
            ClientName = portal == PortalKinds.Client ? "Acme" : null,
        };

    private static async Task<bool> AllowsAsync(ClaimsPrincipal user, string policyName)
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()));
        var policy = policyName.StartsWith(Policies.PermissionPrefix, StringComparison.Ordinal)
            ? (await provider.GetPolicyAsync(policyName))!
            : new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(policyName switch
            {
                Policies.PlatformPortal => new PortalRequirement(PortalKinds.Admin, null, false),
                Policies.ClientPortal => new PortalRequirement(PortalKinds.Client, null, false),
                _ => new PortalRequirement(null, null, true),
            }).Build();

        // the product's own handler decides; "authenticated" is the framework's built-in requirement
        var requirements = policy.Requirements.OfType<PortalRequirement>().ToList();
        var context = new AuthorizationHandlerContext(requirements, user, null);
        await new PortalRequirementHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public void The_cookie_principal_carries_nothing_but_the_session_id()
    {
        var principal = PortalPrincipalFactory.ForCookie("sid-77");

        principal.Claims.Select(c => (c.Type, c.Value)).ShouldBe([(PortalClaims.SessionId, "sid-77")]);
    }

    [Fact]
    public void The_working_principal_is_built_from_the_session_without_any_token()
    {
        var principal = PortalPrincipalFactory.Create(Session(PortalKinds.Admin, false, "clients.read", "dashboard.admin"));

        principal.FindAll(PortalClaims.Permission).Select(c => c.Value).ShouldBe(["clients.read", "dashboard.admin"]);
        principal.HasClaim(PortalClaims.Portal, "admin").ShouldBeTrue();
        principal.IsInRole("SuperAdmin").ShouldBeTrue();
        principal.Claims.ShouldAllBe(c => !c.Value.Contains("access-1") && !c.Value.Contains("refresh-1"));
    }

    [Fact]
    public async Task Platform_users_only_get_the_admin_portal_and_client_users_only_the_client_portal()
    {
        var admin = PortalPrincipalFactory.Create(Session(PortalKinds.Admin));
        var client = PortalPrincipalFactory.Create(Session(PortalKinds.Client));

        (await AllowsAsync(admin, Policies.PlatformPortal)).ShouldBeTrue();
        (await AllowsAsync(admin, Policies.ClientPortal)).ShouldBeFalse();
        (await AllowsAsync(client, Policies.ClientPortal)).ShouldBeTrue();
        (await AllowsAsync(client, Policies.PlatformPortal)).ShouldBeFalse();
        (await AllowsAsync(new ClaimsPrincipal(new ClaimsIdentity()), Policies.PlatformPortal)).ShouldBeFalse();
    }

    [Fact]
    public async Task Permission_policies_follow_the_permission_claims()
    {
        var user = PortalPrincipalFactory.Create(Session(PortalKinds.Admin, false, "clients.read"));

        (await AllowsAsync(user, Policies.Permission("clients.read"))).ShouldBeTrue();
        (await AllowsAsync(user, Policies.Permission("clients.create"))).ShouldBeFalse();
    }

    [Fact]
    public async Task While_a_password_change_is_pending_only_the_signed_in_policy_passes()
    {
        var user = PortalPrincipalFactory.Create(Session(PortalKinds.Admin, mustChange: true, "clients.read"));

        (await AllowsAsync(user, Policies.SignedIn)).ShouldBeTrue();
        (await AllowsAsync(user, Policies.PlatformPortal)).ShouldBeFalse();
        (await AllowsAsync(user, Policies.Permission("clients.read"))).ShouldBeFalse();
    }
}
