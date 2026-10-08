namespace NexaVerify.Domain.Api;

/// <summary>Which window a <see cref="UsageCounter"/> row counts (the value is stored, never renumber).</summary>
public static class UsageCounterKinds
{
    /// <summary>Calls by one credential in one UTC minute.</summary>
    public const byte Minute = 1;

    /// <summary>Calls by one client in one UTC day.</summary>
    public const byte Day = 2;

    /// <summary>Calls to a throttled operation by one principal in one policy window.</summary>
    public const byte Throttle = 3;
}

/// <summary>
/// One shared counter bucket (rate limits and quotas that must hold across API nodes). Rows are tiny, hold no personal data, belong to
/// no tenant, and are bumped by one atomic statement; old buckets are purged by a background job.
/// </summary>
public sealed class UsageCounter
{
    public byte Kind { get; private set; }

    public Guid KeyId { get; private set; }

    /// <summary>Window index: minutes since the epoch (Minute), the day number (Day), or window index of the policy (Throttle).</summary>
    public long Bucket { get; private set; }

    public long Used { get; private set; }

    public DateTime UpdatedAt { get; private set; }
}
