using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.UnitTests.Platform;

public class WebhookOptionsTests
{
    [Fact]
    public void The_lease_is_derived_from_the_worst_case_cycle_by_default()
    {
        var options = new WebhookOptions { BatchSize = 50, Parallelism = 8, TimeoutSeconds = 5 };

        // 7 rounds of (5 s timeout + 5 s margin) + 30 s
        WebhookDispatcher.ResolveLease(options).ShouldBe(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void A_bigger_batch_or_a_longer_timeout_lengthens_the_derived_lease()
    {
        var small = WebhookDispatcher.ResolveLease(new WebhookOptions { BatchSize = 8, Parallelism = 8, TimeoutSeconds = 5 });
        var big = WebhookDispatcher.ResolveLease(new WebhookOptions { BatchSize = 200, Parallelism = 8, TimeoutSeconds = 30 });

        big.ShouldBeGreaterThan(small);
    }

    [Fact]
    public void An_explicit_lease_at_or_above_the_worst_case_is_used_as_is()
    {
        var options = new WebhookOptions { BatchSize = 10, Parallelism = 10, TimeoutSeconds = 5, LeaseSeconds = 300 };

        WebhookDispatcher.ResolveLease(options).ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void An_explicit_lease_shorter_than_the_worst_case_is_refused_because_a_delivery_could_be_sent_twice()
    {
        var options = new WebhookOptions { BatchSize = 50, Parallelism = 8, TimeoutSeconds = 5, LeaseSeconds = 20 };

        Should.Throw<InvalidOperationException>(() => WebhookDispatcher.ResolveLease(options)).Message.ShouldContain("Webhooks:LeaseSeconds");
    }

    [Fact]
    public void Defaults_keep_the_previous_behaviour()
    {
        var options = new WebhookOptions();

        (options.BatchSize, options.Parallelism, options.TimeoutSeconds, options.DisableAfterFailedEvents).ShouldBe((50, 8, 5, 20));
        options.MaxPerEndpointPerCycle.ShouldBeGreaterThan(0);
    }
}
