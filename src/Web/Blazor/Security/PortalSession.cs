using System.Text;

namespace NexaVerify.Web.Security;

/// <summary>
/// Everything the portal remembers about a signed-in browser. It lives only on the server (see <see cref="ISessionStore"/>);
/// the browser holds an opaque, HttpOnly session id and never a token.
/// </summary>
public sealed record PortalSession
{
    public required string Id { get; init; }

    public required string AccessToken { get; init; }

    public required DateTimeOffset AccessTokenExpiresAt { get; init; }

    public required string RefreshToken { get; init; }

    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    public required string FullName { get; init; }

    /// <summary>"admin" for platform staff, "client" for client users (see <see cref="PortalKinds"/>).</summary>
    public required string Portal { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public Guid? ClientId { get; init; }

    public string? ClientName { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public bool MustChangePassword { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public required DateTimeOffset AbsoluteExpiresAt { get; init; }

    // Tokens must never reach logs, exception messages or debugger strings.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id);
        return true;
    }
}

public static class PortalKinds
{
    public const string Admin = "admin";
    public const string Client = "client";
}

/// <summary>Claim types in the portal's own principal (built from the server-side session, never from the cookie).</summary>
public static class PortalClaims
{
    public const string SessionId = "nv:sid";
    public const string Portal = "nv:portal";
    public const string Permission = "nv:perm";
    public const string ClientName = "nv:client-name";
    public const string MustChangePassword = "nv:mcp";
    public const string DisplayName = "nv:display-name";
    public const string RoleName = "nv:role";
}
