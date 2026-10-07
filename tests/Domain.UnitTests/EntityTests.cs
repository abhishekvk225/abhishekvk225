using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.UnitTests;

public class EntityTests
{
    private sealed class Sample : AuditableEntity
    {
    }

    [Fact]
    public void New_entities_get_unique_non_empty_time_ordered_ids()
    {
        var first = new Sample();
        Thread.Sleep(2);
        var second = new Sample();

        first.Id.ShouldNotBe(Guid.Empty);
        second.Id.ShouldNotBe(first.Id);
        // GUID v7 sorts by creation time (byte order of the string form)
        string.CompareOrdinal(first.Id.ToString(), second.Id.ToString()).ShouldBeLessThan(0);
        first.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void Auditable_entities_start_active()
    {
        new Sample().IsActive.ShouldBeTrue();
    }

    [Fact]
    public void Domain_exception_carries_a_stable_code()
    {
        var ex = new DomainException("LICENSE_INVALID_TRANSITION", "nope");

        ex.Code.ShouldBe("LICENSE_INVALID_TRANSITION");
        ex.Message.ShouldBe("nope");
    }
}
