using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class ForwardedHeadersTests
{
    private static (bool Enabled, ForwardedHeadersOptions? Options) Configure(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        var enabled = ForwardedHeadersSetup.Configure(services, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return (enabled, enabled ? services.BuildServiceProvider().GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value : null);
    }

    [Fact]
    public void Disabled_by_default()
    {
        Configure([]).Enabled.ShouldBeFalse();
        Configure(new() { ["ForwardedHeaders:Enabled"] = "false" }).Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Enabling_it_without_a_trust_list_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Configure(new() { ["ForwardedHeaders:Enabled"] = "true" })).Message.ShouldContain("KnownProxies");
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Trusting_the_whole_internet_is_refused(string network)
    {
        Should.Throw<InvalidOperationException>(() => Configure(new() { ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:KnownNetworks:0"] = network }))
            .Message.ShouldContain("/0");
    }

    [Theory]
    [InlineData("KnownProxies:0", "not-an-ip", "not an IP address")]
    [InlineData("KnownNetworks:0", "10.0.0.0", "CIDR")]
    [InlineData("KnownNetworks:0", "10.0.0.0/abc", "CIDR")]
    [InlineData("KnownNetworks:0", "10.0.0.0/99", "CIDR")]
    public void Malformed_entries_fail_with_a_clear_message_not_a_parse_exception(string key, string value, string expected)
    {
        Should.Throw<InvalidOperationException>(() => Configure(new() { ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:" + key] = value }))
            .Message.ShouldContain(expected);
    }

    [Fact]
    public void The_forward_limit_is_bounded()
    {
        Should.Throw<InvalidOperationException>(() => Configure(new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.1",
            ["ForwardedHeaders:ForwardLimit"] = "50",
        })).Message.ShouldContain("ForwardLimit");
    }

    [Fact]
    public void A_valid_list_is_applied()
    {
        var (enabled, options) = Configure(new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5",
            ["ForwardedHeaders:KnownNetworks:0"] = "172.16.0.0/12",
        });

        enabled.ShouldBeTrue();
        options!.KnownProxies.Select(p => p.ToString()).ShouldContain("10.0.0.5");
        options.KnownIPNetworks.Select(n => n.ToString()).ShouldContain("172.16.0.0/12");
        options.ForwardLimit.ShouldBe(1);
    }
}

public class KeyedLockTests
{
    [Fact]
    public async Task Nothing_is_left_behind_after_many_keys_are_used_and_abandoned()
    {
        var gate = new KeyedLock();

        for (var i = 0; i < 1000; i++)
        {
            using (await gate.AcquireAsync("session-" + i))
            {
            }
        }

        gate.ActiveKeys.ShouldBe(0);
    }

    [Fact]
    public async Task A_cancelled_wait_does_not_leak_or_release_somebody_elses_lock()
    {
        var gate = new KeyedLock();
        var holder = await gate.AcquireAsync("k");
        using var cts = new CancellationTokenSource(50);

        await Should.ThrowAsync<OperationCanceledException>(() => gate.AcquireAsync("k", cts.Token));

        gate.ActiveKeys.ShouldBe(1, "the holder still owns it");
        var next = gate.AcquireAsync("k");
        next.IsCompleted.ShouldBeFalse("still held");
        holder.Dispose();
        (await next).Dispose();
        gate.ActiveKeys.ShouldBe(0);
    }

    [Fact]
    public async Task The_lock_is_never_dropped_while_a_caller_holds_it()
    {
        var gate = new KeyedLock();
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using (await gate.AcquireAsync("same"))
            {
                var now = Interlocked.Increment(ref inside);
                maxInside = Math.Max(maxInside, now);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
            }
        }));

        maxInside.ShouldBe(1);
        gate.ActiveKeys.ShouldBe(0);
    }

    [Fact]
    public async Task Different_keys_do_not_block_each_other()
    {
        var gate = new KeyedLock();
        using var a = await gate.AcquireAsync("a");

        var b = gate.AcquireAsync("b");

        b.IsCompletedSuccessfully.ShouldBeTrue();
        (await b).Dispose();
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var gate = new KeyedLock();
        var held = await gate.AcquireAsync("k");

        held.Dispose();
        held.Dispose();

        (await gate.AcquireAsync("k")).Dispose();
        gate.ActiveKeys.ShouldBe(0);
    }
}

public class SessionLockLifecycleTests
{
    [Fact]
    public async Task Abandoned_sessions_leave_no_lock_behind()
    {
        var (store, _, clock) = SessionFixtures.NewStore();

        for (var i = 0; i < 500; i++)
        {
            await store.SaveAsync(SessionFixtures.NewSession(clock, id: "s" + i));
        }

        store.ActiveLocks.ShouldBe(0);
    }

    [Fact]
    public async Task Removing_a_session_while_it_is_being_updated_cannot_bring_it_back()
    {
        var (store, _, clock) = SessionFixtures.NewStore();
        await store.SaveAsync(SessionFixtures.NewSession(clock));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // On another thread: the mutation blocks until released, and a cache that completes synchronously would run it on ours.
        var update = Task.Run(() => store.UpdateAsync("sid-1", s =>
        {
            started.SetResult();
            release.Task.Wait();
            return s with { Email = "changed@x.test" };
        }));
        await started.Task;
        var remove = store.RemoveAsync("sid-1");
        await Task.Delay(50);
        remove.IsCompleted.ShouldBeFalse("the removal waits for the update to finish");
        release.SetResult();
        await Task.WhenAll(update, remove);

        (await store.GetAsync("sid-1")).ShouldBeNull();
        store.ActiveLocks.ShouldBe(0);
    }

    [Fact]
    public async Task Refreshes_of_abandoned_sessions_leave_no_lock_behind()
    {
        var h = new BffHarness();
        for (var i = 0; i < 100; i++)
        {
            var session = SessionFixtures.NewSession(h.Clock, id: "r" + i);
            await h.Store.SaveAsync(session);
            await h.Coordinator.RefreshAsync(session.Id, session.AccessToken, default);
        }

        h.Store.ActiveLocks.ShouldBe(0);
        h.Coordinator.ActiveLocks.ShouldBe(0);
    }
}
