using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Application.Public;
using NexaVerify.Infrastructure.Platform;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.UnitTests.Platform;

public class PublicProtectionTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return await Respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static (TurnstileCaptchaVerifier Verifier, StubHandler Handler) Turnstile(Action<CaptchaOptions>? configure = null)
    {
        var options = new CaptchaOptions { Provider = "turnstile", SiteKey = "site", SecretKey = "super-secret", TimeoutSeconds = 1 };
        configure?.Invoke(options);
        var handler = new StubHandler();
        return (new TurnstileCaptchaVerifier(new HttpClient(handler), Options.Create(options), NullLogger<TurnstileCaptchaVerifier>.Instance), handler);
    }

    [Fact]
    public async Task With_no_provider_everything_passes_and_nothing_is_called()
    {
        var handler = new StubHandler();
        var verifier = new TurnstileCaptchaVerifier(new HttpClient(handler), Options.Create(new CaptchaOptions()), NullLogger<TurnstileCaptchaVerifier>.Instance);

        (await verifier.VerifyAsync(null, "1.2.3.4", default)).ShouldBeTrue();
        handler.Bodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_the_provider_accepts_passes_and_the_secret_goes_only_to_the_provider()
    {
        var (verifier, handler) = Turnstile();
        handler.Respond = _ => Task.FromResult(Json("""{"success":true}"""));

        (await verifier.VerifyAsync("tok", "203.0.113.9", default)).ShouldBeTrue();

        handler.Bodies.Single().ShouldContain("secret=super-secret");
        handler.Bodies.Single().ShouldContain("response=tok");
        handler.Bodies.Single().ShouldContain("remoteip=203.0.113.9");
    }

    [Theory]
    [InlineData("""{"success":false,"error-codes":["invalid-input-response"]}""")]
    [InlineData("""{"success":"true"}""")]
    [InlineData("""{}""")]
    [InlineData("""[true]""")]
    [InlineData("not json")]
    public async Task Anything_but_an_explicit_success_fails(string body)
    {
        var (verifier, handler) = Turnstile();
        handler.Respond = _ => Task.FromResult(Json(body));

        (await verifier.VerifyAsync("tok", null, default)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_token_fails_without_calling_the_provider(string? token)
    {
        var (verifier, handler) = Turnstile();

        (await verifier.VerifyAsync(token, null, default)).ShouldBeFalse();
        (await verifier.VerifyAsync(new string('x', 5000), null, default)).ShouldBeFalse();
        handler.Bodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_errors_and_timeouts_fail_closed()
    {
        var (verifier, handler) = Turnstile();

        handler.Respond = _ => Task.FromResult(Json("""{"success":true}""", HttpStatusCode.InternalServerError));
        (await verifier.VerifyAsync("tok", null, default)).ShouldBeFalse();

        handler.Respond = _ => throw new HttpRequestException("unreachable");
        (await verifier.VerifyAsync("tok", null, default)).ShouldBeFalse();

        handler.Respond = async request =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return Json("""{"success":true}""");
        };
        (await verifier.VerifyAsync("tok", null, default)).ShouldBeFalse(); // 1 s timeout
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_failed_check()
    {
        var (verifier, handler) = Turnstile();
        handler.Respond = async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return Json("""{"success":true}""");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => verifier.VerifyAsync("tok", null, cts.Token));
    }

    // ---- public throttle ----

    private sealed class FakeBackend : ICounterBackend
    {
        private readonly ConcurrentDictionary<(byte, Guid, long), long> _used = new();

        public Task<long> ReserveAsync(byte kind, Guid key, long bucket, long requested, long limit, DateTime now, CancellationToken cancellationToken)
        {
            lock (_used)
            {
                var used = _used.GetValueOrDefault((kind, key, bucket));
                var granted = Math.Max(0, Math.Min(requested, limit - used));
                _used[(kind, key, bucket)] = used + granted;
                return Task.FromResult(granted);
            }
        }

        public Task<int> PurgeAsync(DateTime shortBefore, DateTime dailyBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private static (PublicThrottle Throttle, FakeTimeProvider Time) NewThrottle(SignupOptions options)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 10, 30, 0, TimeSpan.Zero));
        var counters = new SharedWindowCounters(new FakeBackend(), Options.Create(new SharedCounterOptions()), time, NullLogger<SharedWindowCounters>.Instance);
        return (new PublicThrottle(counters, Options.Create(options), time), time);
    }

    private static async Task<int> CountAllowedAsync(PublicThrottle throttle, PublicThrottlePolicy policy, string subject, int attempts)
    {
        var allowed = 0;
        for (var i = 0; i < attempts; i++)
        {
            if (await throttle.TryAcquireAsync(policy, subject, default))
            {
                allowed++;
            }
        }

        return allowed;
    }

    [Fact]
    public async Task Each_policy_enforces_its_own_limit_per_subject_and_the_window_rolls_over()
    {
        var (throttle, time) = NewThrottle(new SignupOptions { MaxSignupsPerIpPerHour = 5, MaxSignupsPerEmailPerDay = 3, MaxContactsPerIpPerHour = 2 });

        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "203.0.113.1", 10)).ShouldBe(5);
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "203.0.113.2", 10)).ShouldBe(5); // another address has its own budget
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.ContactPerIp, "203.0.113.1", 10)).ShouldBe(2); // and so does another policy
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerEmail, "ada@acme.test", 10)).ShouldBe(3);
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerEmail, "  ADA@acme.test ", 10)).ShouldBe(0); // same address, however it is spelled

        time.Advance(TimeSpan.FromHours(1)); // next hour: IP budgets are fresh, the daily email budget is not
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "203.0.113.1", 10)).ShouldBe(5);
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerEmail, "ada@acme.test", 10)).ShouldBe(0);

        time.Advance(TimeSpan.FromDays(1));
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerEmail, "ada@acme.test", 10)).ShouldBe(3);
    }

    [Fact]
    public async Task Ipv6_callers_share_a_budget_per_64_and_mapped_ipv4_equals_ipv4()
    {
        var (throttle, _) = NewThrottle(new SignupOptions { MaxSignupsPerIpPerHour = 3 });

        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "2001:db8:1:2::1", 2)).ShouldBe(2);
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "2001:db8:1:2:ffff::9", 5)).ShouldBe(1);
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "2001:db8:1:3::1", 1)).ShouldBe(1); // a different /64

        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "198.51.100.7", 2)).ShouldBe(2);
        (await CountAllowedAsync(throttle, PublicThrottlePolicy.SignupPerIp, "::ffff:198.51.100.7", 5)).ShouldBe(1);
    }

    [Fact]
    public void Throttle_keys_are_stable_hashes_that_differ_by_policy()
    {
        PublicThrottle.KeyFor(PublicThrottlePolicy.SignupPerIp, "x").ShouldBe(PublicThrottle.KeyFor(PublicThrottlePolicy.SignupPerIp, "x"));
        PublicThrottle.KeyFor(PublicThrottlePolicy.SignupPerIp, "x").ShouldNotBe(PublicThrottle.KeyFor(PublicThrottlePolicy.ContactPerIp, "x"));
    }
}
