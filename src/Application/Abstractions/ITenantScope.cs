namespace NexaVerify.Application.Abstractions;

/// <summary>
/// Explicitly enters a tenant or platform scope for code that runs outside an HTTP request
/// (background jobs, seeding, tests). Dispose to restore the previous scope.
/// </summary>
public interface ITenantScope
{
    IDisposable BeginTenant(Guid clientId);

    /// <summary>Enters platform scope. <paramref name="reason"/> is logged; callers are restricted by an architecture test.</summary>
    IDisposable BeginPlatform(string reason);
}
