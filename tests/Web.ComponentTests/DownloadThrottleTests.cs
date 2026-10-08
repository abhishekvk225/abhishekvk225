using Microsoft.Extensions.Time.Testing;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

public class DownloadThrottleTests
{
    [Fact]
    public void A_session_gets_a_fixed_number_of_exports_per_window_then_waits()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T09:00:00Z"));
        var throttle = new DownloadThrottle(clock);

        for (var i = 0; i < DownloadThrottle.MaxPerWindow; i++)
        {
            throttle.TryAcquire("a", out _).ShouldBeTrue();
        }

        clock.Advance(TimeSpan.FromSeconds(20));
        throttle.TryAcquire("a", out var wait).ShouldBeFalse();
        wait.ShouldBe(TimeSpan.FromSeconds(40));
        throttle.TryAcquire("b", out _).ShouldBeTrue("another session is not affected");

        clock.Advance(TimeSpan.FromSeconds(41));
        throttle.TryAcquire("a", out _).ShouldBeTrue("the window reopened");
    }

    [Fact]
    public void Memory_stays_bounded_when_many_sessions_come_and_go()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T09:00:00Z"));
        var throttle = new DownloadThrottle(clock);
        for (var i = 0; i < 3_000; i++)
        {
            throttle.TryAcquire($"s{i}", out _).ShouldBeTrue();
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        throttle.TryAcquire("fresh", out _).ShouldBeTrue();
        throttle.TryAcquire("s1", out _).ShouldBeTrue("old windows are forgotten");
    }
}
