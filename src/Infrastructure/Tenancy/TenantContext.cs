using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Tenancy;

/// <summary>
/// Resolves the tenant for the current unit of work. An explicit scope (jobs, seeding, tests) wins;
/// otherwise it derives from the authenticated principal. Registered scoped; the ambient scope flows
/// with async execution via <see cref="AsyncLocal{T}"/> so concurrent jobs cannot see each other's tenant.
/// </summary>
public sealed class TenantContext : ITenantContext, ITenantScope
{
    private static readonly AsyncLocal<ScopeState?> Ambient = new();

    private readonly ICurrentUser _currentUser;

    public TenantContext(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    public Guid? ClientId => Ambient.Value is { } scope ? scope.ClientId : FromUser().ClientId;

    public bool IsPlatform => Ambient.Value is { } scope ? scope.IsPlatform : FromUser().IsPlatform;

    public IDisposable BeginTenant(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("A tenant scope requires a non-empty client id.", nameof(clientId));
        }

        return Push(new ScopeState(clientId, false));
    }

    public IDisposable BeginPlatform() => Push(new ScopeState(null, true));

    private (Guid? ClientId, bool IsPlatform) FromUser()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return (null, false);
        }

        // A principal is either bound to one client or is a platform user; never both.
        return _currentUser.IsPlatformUser ? (null, true) : (_currentUser.ClientId, false);
    }

    private static Restore Push(ScopeState state)
    {
        var previous = Ambient.Value;
        Ambient.Value = state;
        return new Restore(previous);
    }

    private sealed record ScopeState(Guid? ClientId, bool IsPlatform);

    private sealed class Restore : IDisposable
    {
        private readonly ScopeState? _previous;
        private bool _disposed;

        public Restore(ScopeState? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
