using Microsoft.Extensions.Logging;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;

namespace NexaVerify.Infrastructure.Tenancy;

/// <summary>
/// Resolves the tenant for the current unit of work. An explicit scope (jobs, auth lookups, seeding, tests) wins;
/// otherwise it derives from the authenticated principal.
/// <para>
/// The ambient scope flows with async execution (<see cref="AsyncLocal{T}"/>) so concurrent flows cannot see each other's
/// tenant, and scopes form a chain: disposing out of order or in another execution context can never resurrect an
/// outer (possibly platform) scope. Note that a scope entered inside an awaited helper does not flow back to its caller.
/// </para>
/// </summary>
public sealed class TenantContext : ITenantContext, ITenantScope
{
    private static readonly AsyncLocal<ScopeNode?> Ambient = new();

    private readonly ICurrentUser _currentUser;
    private readonly ILogger<TenantContext>? _logger;

    public TenantContext(ICurrentUser currentUser, ILogger<TenantContext>? logger = null)
    {
        _currentUser = currentUser;
        _logger = logger;
    }

    public Guid? ClientId => Current() is { } scope ? scope.ClientId : FromUser().ClientId;

    public bool IsPlatform => Current() is { } scope ? scope.IsPlatform : FromUser().IsPlatform;

    public IDisposable BeginTenant(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("A tenant scope requires a non-empty client id.", nameof(clientId));
        }

        // Defence in depth: an authenticated tenant principal can never scope itself to another tenant.
        var user = FromUser();
        if (Current() is null && user.ClientId is { } own && own != clientId)
        {
            throw new TenantViolationException("A tenant principal cannot switch to a different tenant.");
        }

        return Push(clientId, false);
    }

    public IDisposable BeginPlatform(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to enter platform scope.", nameof(reason));
        }

        _logger?.LogDebug("Entering platform scope: {Reason}", reason);
        return Push(null, true);
    }

    private static ScopeNode? Current()
    {
        var node = Ambient.Value;
        while (node is { Disposed: true })
        {
            node = node.Parent;
        }

        return node;
    }

    private (Guid? ClientId, bool IsPlatform) FromUser()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return (null, false);
        }

        // A principal is either bound to one client or is a platform user; never both.
        return _currentUser.IsPlatformUser ? (null, true) : (_currentUser.ClientId, false);
    }

    private static ScopeNode Push(Guid? clientId, bool isPlatform)
    {
        var node = new ScopeNode(clientId, isPlatform, Current());
        Ambient.Value = node;
        return node;
    }

    private sealed class ScopeNode : IDisposable
    {
        public ScopeNode(Guid? clientId, bool isPlatform, ScopeNode? parent)
        {
            ClientId = clientId;
            IsPlatform = isPlatform;
            Parent = parent;
        }

        public Guid? ClientId { get; }

        public bool IsPlatform { get; }

        public ScopeNode? Parent { get; }

        // Volatile: may be set from another execution context (Task.Run / fire-and-forget disposal).
        public volatile bool Disposed;

        public void Dispose()
        {
            Disposed = true;
            if (ReferenceEquals(Ambient.Value, this))
            {
                Ambient.Value = Parent;
            }
        }
    }
}
