using System.Net;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class TokenRefreshTests
{
    private const string Ok = "{\"value\":1}";

    private sealed record Payload(int Value);

    private static HttpResponseMessage Unauthorized() => ScriptedApi.Json(HttpStatusCode.Unauthorized, "{\"code\":\"TOKEN_EXPIRED\",\"correlationId\":\"c-1\"}");

    [Fact]
    public async Task The_sessions_bearer_token_is_attached_server_side()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        var result = await h.Gateway.GetAsync<Payload>("admin/dashboard");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(1);
        h.Api.Seen.Single().Authorization.ShouldBe("Bearer access-1");
        h.Api.Seen.Single().Path.ShouldBe("/api/v1/admin/dashboard");
        h.Exchange.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Anonymous_calls_carry_no_credentials_at_all()
    {
        var h = new BffHarness(sessionId: null);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        var result = await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "auth/login", new { email = "a@b.test" }, default, ApiCallOptions.None);

        result.IsSuccess.ShouldBeTrue();
        h.Api.Seen.Single().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_session_nothing_is_sent_and_the_caller_learns_the_session_ended()
    {
        var h = new BffHarness(sessionId: null);

        var result = await h.Gateway.GetAsync<Payload>("admin/dashboard");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("SESSION_EXPIRED");
        h.Api.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_token_is_refreshed_and_the_call_retried_once_with_the_same_body()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = r => r.Authorization == "Bearer access-2" ? ScriptedApi.Json(HttpStatusCode.OK, Ok) : Unauthorized();

        var result = await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "admin/licenses/x/adjust", new { credits = 5, reason = "goodwill" });

        result.IsSuccess.ShouldBeTrue();
        h.Api.Seen.Count.ShouldBe(2);
        h.Api.Seen[0].Authorization.ShouldBe("Bearer access-1");
        h.Api.Seen[1].Authorization.ShouldBe("Bearer access-2");
        h.Api.Seen[1].Body.ShouldBe(h.Api.Seen[0].Body);
        h.Api.Seen[1].Body!.ShouldContain("goodwill");
        h.Exchange.Calls.ShouldBe(1);
        h.Exchange.RefreshTokensSeen.ShouldBe(["refresh-1"]);
        var stored = (await h.Store.GetAsync("sid-1"))!;
        (stored.AccessToken, stored.RefreshToken).ShouldBe(("access-2", "refresh-2"));
    }

    [Fact]
    public async Task Parallel_calls_with_a_rejected_token_trigger_exactly_one_refresh()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = r => r.Authorization == "Bearer access-2" ? ScriptedApi.Json(HttpStatusCode.OK, Ok) : Unauthorized();
        var release = new TaskCompletionSource();
        h.Exchange.OnExchange = async (_, n) =>
        {
            await release.Task; // hold the refresh open while every caller piles up behind it
            return FakeExchange.Tokens("access-2", "refresh-2");
        };

        var calls = Enumerable.Range(0, 8).Select(_ => h.Gateway.GetAsync<Payload>("admin/dashboard")).ToList();
        await Task.Delay(100);
        release.SetResult();
        var results = await Task.WhenAll(calls);

        results.ShouldAllBe(r => r.IsSuccess);
        h.Exchange.Calls.ShouldBe(1, "a second refresh with the same token would look like token theft and revoke the session");
    }

    [Fact]
    public async Task A_token_about_to_expire_is_refreshed_before_the_call()
    {
        var h = new BffHarness();
        await h.SignInAsync(accessLifetimeSeconds: 10); // inside the 30 s skew
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        (await h.Gateway.GetAsync<Payload>("x")).IsSuccess.ShouldBeTrue();

        h.Api.Seen.Single().Authorization.ShouldBe("Bearer access-2");
        h.Exchange.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Parallel_calls_on_an_expired_token_share_one_refresh()
    {
        var h = new BffHarness();
        await h.SignInAsync(accessLifetimeSeconds: -5);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => h.Gateway.GetAsync<Payload>("x")));

        results.ShouldAllBe(r => r.IsSuccess);
        h.Exchange.Calls.ShouldBe(1);
        h.Api.Seen.ShouldAllBe(r => r.Authorization == "Bearer access-2");
    }

    [Fact]
    public async Task Consecutive_refreshes_use_the_rotated_refresh_token()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = r => r.Authorization == "Bearer access-1" || r.Authorization == "Bearer access-2" ? Unauthorized() : ScriptedApi.Json(HttpStatusCode.OK, Ok);

        (await h.Gateway.GetAsync<Payload>("x")).IsSuccess.ShouldBeFalse(); // access-1 -> access-2, still rejected, no endless loop
        (await h.Gateway.GetAsync<Payload>("x")).IsSuccess.ShouldBeTrue(); // access-2 -> access-3

        h.Exchange.RefreshTokensSeen.ShouldBe(["refresh-1", "refresh-2"]);
    }

    [Fact]
    public async Task A_second_rejection_after_refreshing_is_returned_not_retried_forever()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => Unauthorized();

        var result = await h.Gateway.GetAsync<Payload>("x");

        result.IsSuccess.ShouldBeFalse();
        h.Api.Seen.Count.ShouldBe(2);
        h.Exchange.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refresh_token_the_api_refuses_ends_the_session(HttpStatusCode refusal)
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => Unauthorized();
        h.Exchange.OnExchange = (_, _) => Task.FromResult(ApiResult<NexaVerify.Contracts.Identity.LoginResponse>.Fail("TOKEN_EXPIRED", "Reuse detected.", "c-9", (int)refusal));

        var result = await h.Gateway.GetAsync<Payload>("x");

        result.Error!.Code.ShouldBe("SESSION_EXPIRED");
        (await h.Store.GetAsync("sid-1")).ShouldBeNull("reuse detection / revocation signs the user out");
        // later calls do not even reach the network
        var before = h.Api.Seen.Count;
        (await h.Gateway.GetAsync<Payload>("x")).Error!.Code.ShouldBe("SESSION_EXPIRED");
        h.Api.Seen.Count.ShouldBe(before);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task A_temporarily_unavailable_api_does_not_sign_the_user_out(int status)
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => Unauthorized();
        h.Exchange.OnExchange = (_, _) => Task.FromResult(ApiResult<NexaVerify.Contracts.Identity.LoginResponse>.Fail("INTERNAL_ERROR", "down", null, status));

        var result = await h.Gateway.GetAsync<Payload>("x");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldNotBe("SESSION_EXPIRED");
        (await h.Store.GetAsync("sid-1")).ShouldNotBeNull();
        (await h.Store.GetAsync("sid-1"))!.RefreshToken.ShouldBe("refresh-1");
    }

    [Fact]
    public async Task A_caller_that_gives_up_mid_refresh_cannot_lose_the_rotated_token()
    {
        var h = new BffHarness();
        var session = await h.SignInAsync();
        var release = new TaskCompletionSource();
        h.Exchange.OnExchange = async (_, _) =>
        {
            await release.Task;
            return FakeExchange.Tokens("access-2", "refresh-2");
        };
        using var cts = new CancellationTokenSource();

        var refresh = h.Coordinator.RefreshAsync(session.Id, session.AccessToken, cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        release.SetResult();
        var outcome = await refresh;

        outcome.Status.ShouldBe(RefreshStatus.Refreshed);
        (await h.Store.GetAsync("sid-1"))!.RefreshToken.ShouldBe("refresh-2", "the API already rotated it; the new one must be kept");
    }

    [Fact]
    public async Task A_caller_arriving_after_someone_else_refreshed_just_picks_up_the_new_token()
    {
        var h = new BffHarness();
        var session = await h.SignInAsync();
        await h.Store.UpdateAsync("sid-1", s => s with { AccessToken = "access-9", AccessTokenExpiresAt = h.Clock.GetUtcNow().AddMinutes(10) });

        var outcome = await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default);

        outcome.ShouldBe(new RefreshOutcome(RefreshStatus.Refreshed, "access-9"));
        h.Exchange.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_refresh_exchange_never_runs_for_an_unknown_session()
    {
        var h = new BffHarness();

        var outcome = await h.Coordinator.RefreshAsync("nope", "x", default);

        outcome.Status.ShouldBe(RefreshStatus.SessionEnded);
        h.Exchange.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Using_the_session_slides_the_idle_window()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, Ok);

        h.Clock.Advance(TimeSpan.FromMinutes(20));
        await h.Gateway.GetAsync<Payload>("x");
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        h.Exchange.OnExchange = (_, _) => Task.FromResult(FakeExchange.Tokens("access-2", "refresh-2")); // the 15 min token has expired by now

        (await h.Gateway.GetAsync<Payload>("x")).IsSuccess.ShouldBeTrue();
    }
}
