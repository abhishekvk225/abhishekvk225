namespace NexaVerify.Application.Abstractions;

/// <summary>
/// Explicitly enters a tenant or platform scope for code that runs outside an HTTP request
/// (background jobs, seeding, tests). Dispose to restore the previous scope.
/// </summary>
public interface ITenantScope
{
    IDisposable BeginTenant(Guid clientId);

    IDisposable BeginPlatform();
}
