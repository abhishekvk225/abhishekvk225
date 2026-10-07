namespace NexaVerify.Application.Common;

/// <summary>
/// A write attempted to create, change or remove data outside the current tenant. Always a bug or an attack;
/// never expected business flow. Surfaces to clients as a generic 404, never with details.
/// </summary>
public sealed class TenantViolationException : InvalidOperationException
{
    public TenantViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>Optimistic-concurrency conflict (row changed since it was read). Maps to 409 CONCURRENCY_CONFLICT.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(Exception inner)
        : base("The resource was modified by someone else. Reload and try again.", inner)
    {
    }
}

/// <summary>A unique index or constraint rejected the write. Maps to 409 CONFLICT.</summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException(Exception inner)
        : base("A record with the same unique value already exists.", inner)
    {
    }
}
