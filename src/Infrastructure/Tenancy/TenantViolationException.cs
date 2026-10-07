namespace NexaVerify.Infrastructure.Tenancy;

/// <summary>
/// A write attempted to create, change or remove data outside the current tenant. Always a bug or an
/// attack; never expected business flow. Surfaces to clients as a generic 404/500, never with details.
/// </summary>
public sealed class TenantViolationException : InvalidOperationException
{
    public TenantViolationException(string message)
        : base(message)
    {
    }
}
