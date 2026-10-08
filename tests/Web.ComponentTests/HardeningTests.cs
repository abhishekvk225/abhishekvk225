using System.Net;
using Microsoft.AspNetCore.Http;
using NexaVerify.Contracts.Identity;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class ClientAddressForwardingTests
{
    private sealed record Payload(int Value);

    private const string Ok = "{\"value\":1}";

    [Fact]
    public async Task Calls_made_from_a_circuit_carry_the_persons_address()
    {
        var h = new BffHarness(address: new ClientAddress { Value = "203.0.113.7" });
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        await h.Gateway.GetAsync<Payload>("x");
        await h.Gateway.SendAsync(HttpMethod.Post, "auth/forgot-password", new { email = "a@b.test" }, default, ApiCallOptions.None);

        h.Api.Seen.ShouldAllBe(r => r.ForwardedFor == "203.0.113.7");
    }

    [Fact]
    public async Task An_explicit_address_wins_and_no_address_means_no_header()
    {
        var h = new BffHarness(address: new ClientAddress { Value = "203.0.113.7" });
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        await h.Gateway.GetAsync<Payload>("x", default, new ApiCallOptions { ClientIp = "198.51.100.2" });
        var bare = new BffHarness();
        await bare.SignInAsync();
        bare.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);
        await bare.Gateway.GetAsync<Payload>("x");

        h.Api.Seen.Single().ForwardedFor.ShouldBe("198.51.100.2");
        bare.Api.Seen.Single().ForwardedFor.ShouldBeNull();
    }

    [Fact]
    public async Task Forgot_and_reset_password_go_out_with_the_persons_address()
    {
        var h = new BffHarness(sessionId: null, address: new ClientAddress { Value = "203.0.113.9" });
        h.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
        var auth = new AuthApiClient(h.Gateway);

        await auth.ForgotPasswordAsync(new ForgotPasswordModel { Email = "a@b.test" });
        await auth.ResetPasswordAsync(new ResetPasswordModel { Email = "a@b.test", Token = "t", NewPassword = "x" });

        h.Api.Seen.Count.ShouldBe(2);
        h.Api.Seen.ShouldAllBe(r => r.ForwardedFor == "203.0.113.9" && r.Authorization == null);
    }

    [Fact]
    public async Task Token_refresh_uses_the_address_stored_at_sign_in()
    {
        var h = new BffHarness();
        var session = SessionFixtures.NewSession(h.Clock) with { ClientIp = "203.0.113.50" };
        await h.Store.SaveAsync(session);
        h.Api.Respond = r => r.Authorization == "Bearer access-2" ? ScriptedApi.Json(HttpStatusCode.OK, Ok) : ScriptedApi.Json(HttpStatusCode.Unauthorized, "{}");

        await h.Gateway.GetAsync<Payload>("x");

        h.Exchange.ClientIpsSeen.ShouldBe(["203.0.113.50"]);
    }

    [Fact]
    public async Task Signing_in_stores_the_address_on_the_session()
    {
        var h = new BffHarness();
        var auth = new PortalAuth(new FakeAuthApi(), h.Store, h.Coordinator, h.Clock, Microsoft.Extensions.Options.Options.Create(h.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PortalAuth>.Instance);

        var session = (await auth.SignInAsync("a@b.test", "pw", default, "203.0.113.77")).Value;

        (await h.Store.GetAsync(session.Id))!.ClientIp.ShouldBe("203.0.113.77");
    }
}

public class PermissionRefreshTests
{
    private static MeResponse Me(bool platform, string[] permissions, string[]? roles = null, bool mustChange = false) =>
        new(new UserSummary(Guid.NewGuid(), "admin@nexaverify.test", "Ada Admin", platform, platform ? null : Guid.NewGuid(), roles ?? ["SuperAdmin"]), permissions, mustChange, null);

    private static async Task<(BffHarness H, PortalSession Session)> StartAsync()
    {
        var h = new BffHarness();
        var session = await h.SignInAsync();
        return (h, session);
    }

    [Fact]
    public async Task Rotating_the_token_also_refreshes_roles_and_permissions()
    {
        var (h, session) = await StartAsync();
        h.Exchange.OnProfile = () => ApiResult<MeResponse>.Ok(Me(true, ["dashboard.admin"], ["Support"]));

        var outcome = await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default);

        outcome.Status.ShouldBe(RefreshStatus.Refreshed);
        var stored = (await h.Store.GetAsync(session.Id))!;
        stored.Permissions.ShouldBe(["dashboard.admin"]);
        stored.Roles.ShouldBe(["Support"]);
    }

    [Fact]
    public async Task A_new_forced_password_change_is_picked_up()
    {
        var (h, session) = await StartAsync();
        h.Exchange.OnProfile = () => ApiResult<MeResponse>.Ok(Me(true, ["dashboard.admin"], mustChange: true));

        await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default);

        (await h.Store.GetAsync(session.Id))!.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public async Task An_account_that_moved_between_portals_is_signed_out()
    {
        var (h, session) = await StartAsync();
        h.Exchange.OnProfile = () => ApiResult<MeResponse>.Ok(Me(platform: false, ["dashboard.client"]));

        var outcome = await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default);

        outcome.Status.ShouldBe(RefreshStatus.SessionEnded);
        (await h.Store.GetAsync(session.Id)).ShouldBeNull();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_profile_ends_the_session(int status)
    {
        var (h, session) = await StartAsync();
        h.Exchange.OnProfile = () => ApiResult<MeResponse>.Fail("FORBIDDEN", "no", null, status);

        (await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default)).Status.ShouldBe(RefreshStatus.SessionEnded);
        (await h.Store.GetAsync(session.Id)).ShouldBeNull();
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    public async Task A_profile_that_cannot_be_read_keeps_the_previous_snapshot(int status)
    {
        var (h, session) = await StartAsync();
        h.Exchange.OnProfile = () => ApiResult<MeResponse>.Fail("INTERNAL_ERROR", "down", null, status);

        var outcome = await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default);

        outcome.Status.ShouldBe(RefreshStatus.Refreshed);
        (await h.Store.GetAsync(session.Id))!.Permissions.ShouldBe(session.Permissions);
    }

    [Fact]
    public void A_circuit_notices_when_its_view_of_the_user_is_out_of_date()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SessionFixtures.Start);
        var session = SessionFixtures.NewSession(clock);
        var principal = PortalPrincipalFactory.Create(session);

        PortalPrincipalFactory.SameAuthorization(principal, session).ShouldBeTrue();
        PortalPrincipalFactory.SameAuthorization(principal, session with { Permissions = [.. session.Permissions, "clients.create"] }).ShouldBeFalse("permission added");
        PortalPrincipalFactory.SameAuthorization(principal, session with { Permissions = ["dashboard.admin"] }).ShouldBeFalse("permission removed");
        PortalPrincipalFactory.SameAuthorization(principal, session with { Roles = ["Support"] }).ShouldBeFalse("role changed");
        PortalPrincipalFactory.SameAuthorization(principal, session with { MustChangePassword = true }).ShouldBeFalse("password change now required");
        PortalPrincipalFactory.SameAuthorization(principal, session with { Portal = PortalKinds.Client }).ShouldBeFalse("portal changed");
    }
}

public class TimeoutAndRedirectTests
{
    private sealed record Payload(int Value);

    [Fact]
    public async Task A_call_that_runs_past_its_limit_times_out_with_its_own_message_not_as_unreachable()
    {
        var h = new BffHarness(apiOptions: new ApiClientOptions { TimeoutSeconds = 1 });
        await h.SignInAsync();
        h.Api.TokenAwareDelay = TimeSpan.FromSeconds(5);

        var error = (await h.Gateway.GetAsync<Payload>("x")).Error!;

        error.Code.ShouldBe(ApiGateway.TimeoutCode);
        error.Message.ShouldContain("longer than expected");
        error.Message.ShouldNotContain("can't reach");
    }

    [Fact]
    public async Task Long_running_calls_get_their_own_longer_limit()
    {
        var h = new BffHarness(apiOptions: new ApiClientOptions { TimeoutSeconds = 1, LongRunningTimeoutSeconds = 10 });
        await h.SignInAsync();
        h.Api.TokenAwareDelay = TimeSpan.FromMilliseconds(1500);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"value\":1}");

        (await h.Gateway.GetAsync<Payload>("x")).Error!.Code.ShouldBe(ApiGateway.TimeoutCode);
        (await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "admin/licensing/verify-ledger", null, default, new ApiCallOptions { LongRunning = true })).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_mistaken_for_a_timeout()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.TokenAwareDelay = TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(100);

        await Should.ThrowAsync<OperationCanceledException>(() => h.Gateway.GetAsync<Payload>("x", cts.Token));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_from_the_api_is_an_error_never_followed(int status)
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        };

        var error = (await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "auth/login", new { password = "x" }, default, ApiCallOptions.None)).Error!;

        error.Code.ShouldBe("API_UNAVAILABLE");
        error.Message.ShouldNotContain("evil");
        h.Api.Seen.Count.ShouldBe(1);
    }
}

public class SignOutRequestOriginTests
{
    [Theory]
    [InlineData("same-origin", true)]
    [InlineData("none", true)]
    [InlineData("cross-site", false)]
    [InlineData("same-site", false)]
    [InlineData("", false)]
    [InlineData("anything", false)]
    public void Only_the_portal_itself_may_trigger_sign_out(string header, bool allowed)
    {
        var context = new DefaultHttpContext();
        if (header.Length > 0)
        {
            context.Request.Headers["Sec-Fetch-Site"] = header;
        }

        AuthEndpoints.IsSameOriginNavigation(context.Request).ShouldBe(allowed);
    }
}

public class LoadStateTests
{
    [Fact]
    public async Task A_newer_load_wins_even_when_an_older_one_finishes_last()
    {
        using var state = new LoadState<string>();
        var older = new TaskCompletionSource<ApiResult<string>>();
        var olderLoad = state.LoadAsync(_ => older.Task);

        var newerWon = await state.LoadAsync(_ => Task.FromResult(ApiResult<string>.Ok("B")));
        older.SetResult(ApiResult<string>.Ok("A"));

        newerWon.ShouldBeTrue();
        (await olderLoad).ShouldBeFalse();
        state.Result!.Value.ShouldBe("B");
    }

    [Fact]
    public async Task Starting_a_load_cancels_the_previous_one_and_disposing_cancels_the_current()
    {
        var state = new LoadState<string>();
        CancellationToken first = default;
        CancellationToken second = default;

        var firstLoad = state.LoadAsync(async ct =>
        {
            first = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return ApiResult<string>.Ok("never");
        });
        var secondLoad = state.LoadAsync(async ct =>
        {
            second = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return ApiResult<string>.Ok("never");
        });

        first.IsCancellationRequested.ShouldBeTrue();
        (await firstLoad).ShouldBeFalse();
        second.IsCancellationRequested.ShouldBeFalse();
        state.Dispose();
        second.IsCancellationRequested.ShouldBeTrue();
        (await secondLoad).ShouldBeFalse();
        state.Result.ShouldBeNull();
    }

    [Fact]
    public async Task Reloading_can_keep_the_current_content_on_screen()
    {
        using var state = new LoadState<string>();
        await state.LoadAsync(_ => Task.FromResult(ApiResult<string>.Ok("one")));
        var gate = new TaskCompletionSource<ApiResult<string>>();

        var reload = state.LoadAsync(_ => gate.Task, keepCurrent: true);
        state.Result!.Value.ShouldBe("one");
        gate.SetResult(ApiResult<string>.Ok("two"));
        await reload;

        state.Result!.Value.ShouldBe("two");
    }

    [Fact]
    public async Task Failures_are_kept_as_results_and_set_replaces_whatever_is_loading()
    {
        using var state = new LoadState<string>();
        await state.LoadAsync(_ => Task.FromResult(ApiResult<string>.Fail("X", "bad")));
        state.Result!.IsSuccess.ShouldBeFalse();

        state.Set(ApiResult<string>.Ok("fresh"));

        state.Result!.Value.ShouldBe("fresh");
    }
}
