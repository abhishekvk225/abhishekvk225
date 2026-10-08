using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.UnitTests.Platform;

public class SharedWindowCountersTests
{
    /// <summary>Stands in for the SQL table: one authoritative count per bucket, grants capped at the limit, like the real statement.</summary>
    private sealed class FakeBackend : ICounterBackend
    {
        private readonly ConcurrentDictionary<(byte, Guid, long), long> _used = new();
        private readonly object _gate = new();

        public int Calls;

        public bool Fail { get; set; }

        public long Used(byte kind, Guid key, long bucket) => _used.GetValueOrDefault((kind, key, bucket));

        public Task<long> ReserveAsync(byte kind, Guid key, long bucket, long requested, long limit, DateTime now, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Fail)
            {
                throw new InvalidOperationException("database down");
            }

            lock (_gate)
            {
                var used = _used.GetValueOrDefault((kind, key, bucket));
                var granted = Math.Max(0, Math.Min(requested, limit - used));
                _used[(kind, key, bucket)] = used + granted;
                return Task.FromResult(granted);
            }
        }

        public Task<int> PurgeAsync(DateTime shortBefore, DateTime dailyBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private static readonly Guid Key = Guid.NewGuid();

    private static SharedWindowCounters Node(FakeBackend backend, FakeTimeProvider time, Action<SharedCounterOptions>? configure = null)
    {
        var options = new SharedCounterOptions();
        configure?.Invoke(options);
        return new SharedWindowCounters(backend, Options.Create(options), time, NullLogger<SharedWindowCounters>.Instance);
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 3, 1, 10, 0, 30, TimeSpan.Zero));

    private static async Task<int> TakeManyAsync(SharedWindowCounters node, long limit, int attempts, long bucket = 1)
    {
        var granted = 0;
        for (var i = 0; i < attempts; i++)
        {
            if (await node.TryTakeAsync(UsageCounterKinds.Minute, Key, bucket, limit, default))
            {
                granted++;
            }
        }

        return granted;
    }

    [Fact]
    public async Task Most_calls_are_served_from_memory_and_the_database_is_asked_for_blocks_of_permits()
    {
        var backend = new FakeBackend();
        var node = Node(backend, Clock()); // limit 200 / divisor 20 = blocks of 10

        (await TakeManyAsync(node, 200, 200)).ShouldBe(200);

        backend.Calls.ShouldBe(20);
        backend.Used(UsageCounterKinds.Minute, Key, 1).ShouldBe(200);
    }

    [Fact]
    public async Task Two_nodes_together_never_get_more_than_the_limit()
    {
        var backend = new FakeBackend();
        var time = Clock();
        var a = Node(backend, time);
        var b = Node(backend, time);

        var granted = 0;
        for (var i = 0; i < 60; i++)
        {
            granted += await TakeManyAsync(i % 2 == 0 ? a : b, 25, 1);
        }

        granted.ShouldBeLessThanOrEqualTo(25);
        granted.ShouldBeGreaterThan(20, "permits stranded in the other node's block may be lost, but not most of them");
        backend.Used(UsageCounterKinds.Minute, Key, 1).ShouldBe(25);
    }

    [Fact]
    public async Task A_small_limit_is_exact_across_nodes_because_permits_are_reserved_one_at_a_time()
    {
        var backend = new FakeBackend();
        var time = Clock();
        var nodes = new[] { Node(backend, time), Node(backend, time), Node(backend, time) };

        var granted = 0;
        for (var i = 0; i < 30; i++)
        {
            granted += await TakeManyAsync(nodes[i % 3], 7, 1);
        }

        granted.ShouldBe(7);
    }

    [Fact]
    public async Task Concurrent_callers_on_several_nodes_never_overshoot()
    {
        var backend = new FakeBackend();
        var time = Clock();
        var nodes = Enumerable.Range(0, 4).Select(_ => Node(backend, time)).ToArray();

        var results = await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(() => TakeManyAsync(nodes[i % 4], 100, 1))));

        results.Sum().ShouldBeLessThanOrEqualTo(100);
        results.Sum().ShouldBeGreaterThan(70);
    }

    [Fact]
    public async Task A_new_window_starts_with_a_fresh_budget()
    {
        var backend = new FakeBackend();
        var node = Node(backend, Clock());

        (await TakeManyAsync(node, 5, 10, bucket: 1)).ShouldBe(5);
        (await TakeManyAsync(node, 5, 10, bucket: 2)).ShouldBe(5);
    }

    [Fact]
    public async Task A_refused_key_is_refused_from_memory_until_the_recheck_interval_passes()
    {
        var backend = new FakeBackend();
        var time = Clock();
        var node = Node(backend, time, o => o.ExhaustedRecheckSeconds = 5);
        (await TakeManyAsync(node, 3, 3)).ShouldBe(3);
        var callsWhenFull = backend.Calls;

        (await TakeManyAsync(node, 3, 50)).ShouldBe(0);
        backend.Calls.ShouldBe(callsWhenFull + 1, "one database call confirmed the exhaustion, the other 49 refusals were local");

        time.Advance(TimeSpan.FromSeconds(6));
        await TakeManyAsync(node, 3, 1);
        backend.Calls.ShouldBe(callsWhenFull + 2, "after the interval the database is asked again");
    }

    [Fact]
    public async Task A_returned_permit_can_be_used_again_by_the_same_node()
    {
        var backend = new FakeBackend();
        var node = Node(backend, Clock());
        (await TakeManyAsync(node, 2, 2)).ShouldBe(2);

        node.Return(UsageCounterKinds.Minute, Key, 1);

        (await TakeManyAsync(node, 2, 1)).ShouldBe(1);
    }

    [Fact]
    public async Task When_the_database_is_down_a_node_keeps_limiting_on_its_own_and_recovers_afterwards()
    {
        var backend = new FakeBackend { Fail = true };
        var time = Clock();
        var node = Node(backend, time, o => o.ExhaustedRecheckSeconds = 0);

        (await TakeManyAsync(node, 10, 25)).ShouldBe(10, "per-node fallback still enforces the limit instead of failing the request");

        backend.Fail = false;
        (await TakeManyAsync(node, 10, 5, bucket: 2)).ShouldBe(5);
        backend.Used(UsageCounterKinds.Minute, Key, 2).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task With_sharing_switched_off_the_database_is_never_touched()
    {
        var backend = new FakeBackend();
        var node = Node(backend, Clock(), o => o.Shared = false);

        (await TakeManyAsync(node, 8, 20)).ShouldBe(8);

        backend.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed_as_a_database_failure()
    {
        var backend = new CancellingBackend();
        var node = new SharedWindowCounters(backend, Options.Create(new SharedCounterOptions()), Clock(), NullLogger<SharedWindowCounters>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => node.TryTakeAsync(UsageCounterKinds.Day, Key, 1, 10, new CancellationToken(true)));
    }

    private sealed class CancellingBackend : ICounterBackend
    {
        public Task<long> ReserveAsync(byte kind, Guid key, long bucket, long requested, long limit, DateTime now, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);

        public Task<int> PurgeAsync(DateTime shortBefore, DateTime dailyBefore, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    [Fact]
    public async Task Idle_keys_are_forgotten_so_memory_stays_bounded()
    {
        var backend = new FakeBackend();
        var time = Clock();
        var node = Node(backend, time);
        for (var i = 0; i < 500; i++)
        {
            await node.TryTakeAsync(UsageCounterKinds.Throttle, Guid.NewGuid(), 1, 10, default);
        }

        node.TrackedKeys.ShouldBe(500);

        time.Advance(TimeSpan.FromMinutes(30));
        await node.TryTakeAsync(UsageCounterKinds.Throttle, Guid.NewGuid(), 2, 10, default);

        node.TrackedKeys.ShouldBeLessThan(5);
    }

    [Fact]
    public void Throttle_policies_have_their_own_limit_and_unknown_ones_are_rejected()
    {
        var options = new ThrottleOptions { ExportsPerMinute = 3, WebhookTestsPerMinute = 4 };

        options.LimitFor("exports").ShouldBe(3);
        options.LimitFor("webhook-test").ShouldBe(4);
        options.LimitFor("dashboards").ShouldBe(60);
        Should.Throw<ArgumentOutOfRangeException>(() => options.LimitFor("nope"));
    }

    [Fact]
    public async Task The_principal_throttle_counts_per_policy_and_per_principal()
    {
        var backend = new FakeBackend();
        var time = Clock();
        var throttle = new PrincipalThrottle(Node(backend, time), Options.Create(new ThrottleOptions { ExportsPerMinute = 2, DashboardsPerMinute = 2 }), time);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        (await throttle.TryAcquireAsync("exports", alice, default)).Error.ShouldBeNull();
        (await throttle.TryAcquireAsync("exports", alice, default)).Error.ShouldBeNull();
        var refused = await throttle.TryAcquireAsync("exports", alice, default);

        refused.Error.ShouldNotBeNull().Code.ShouldBe("RATE_LIMITED");
        refused.RetryAfter.ShouldBeInRange(TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(31));
        (await throttle.TryAcquireAsync("exports", bob, default)).Error.ShouldBeNull("another principal has its own budget");
        (await throttle.TryAcquireAsync("dashboards", alice, default)).Error.ShouldBeNull("another policy has its own budget");
        PrincipalThrottle.KeyFor("exports", alice).ShouldBe(PrincipalThrottle.KeyFor("exports", alice));
        PrincipalThrottle.KeyFor("exports", alice).ShouldNotBe(PrincipalThrottle.KeyFor("dashboards", alice));
    }
}
