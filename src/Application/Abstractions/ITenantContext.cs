namespace NexaVerify.Application.Abstractions;

/// <summary>
/// The tenant the current unit of work runs for. Resolved only from the authenticated credential
/// (JWT / API key) or an explicit scope — never from request bodies, queries or routes.
/// Fail-closed: when neither <see cref="ClientId"/> nor <see cref="IsPlatform"/> is set, no tenant data is visible.
/// </summary>
public interface ITenantContext
{
    /// <summary>Client the work is scoped to; null for platform scope or when unresolved.</summary>
    Guid? ClientId { get; }

    /// <summary>True for platform (Super Admin / system job) scope that may read across tenants.</summary>
    bool IsPlatform { get; }
}
