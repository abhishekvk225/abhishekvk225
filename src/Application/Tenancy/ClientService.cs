using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Tenancy;

public interface IClientService
{
    Task<Result<PagedResult<ClientListItemDto>>> ListAsync(ClientListQuery query, CancellationToken cancellationToken);

    Task<Result<ClientDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken cancellationToken);

    Task<Result<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken cancellationToken);

    Task<Result<ClientDto>> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<ClientDto>> DeactivateAsync(Guid id, ClientStatusRequest request, CancellationToken cancellationToken);

    Task<Result<ClientDto>> SuspendAsync(Guid id, ClientStatusRequest request, CancellationToken cancellationToken);

    Task<Result<PagedResult<ClientUserDto>>> ListUsersAsync(Guid id, PageRequest page, CancellationToken cancellationToken);

    Task<Result> ResetUserPasswordAsync(Guid clientId, Guid userId, CancellationToken cancellationToken);

    Task<Result<PagedResult<AuditLogDto>>> GetActivityAsync(Guid id, ActivityQuery query, CancellationToken cancellationToken);

    Task<Result<PagedResult<LoginHistoryDto>>> GetLoginsAsync(Guid id, ActivityQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Platform-side client management: create (with an invited first admin — no password is ever shared), edit, status
/// lifecycle with immediate session invalidation, support-initiated password reset, and the client's activity trail.
/// </summary>
public sealed class ClientService : IClientService
{
    private readonly IClientRepository _clients;
    private readonly IClientQueries _queries;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IClientMembershipRepository _memberships;
    private readonly IClientKeyProvisioner _keys;
    private readonly IPasswordHasher _hasher;
    private readonly ISecureTokenService _secure;
    private readonly IPasswordResetService _resetService;
    private readonly IClientAccessGuard _guard;
    private readonly ISessionValidator _sessions;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public ClientService(
        IClientRepository clients,
        IClientQueries queries,
        IUserRepository users,
        IRoleRepository roles,
        IClientMembershipRepository memberships,
        IClientKeyProvisioner keys,
        IPasswordHasher hasher,
        ISecureTokenService secure,
        IPasswordResetService resetService,
        IClientAccessGuard guard,
        ISessionValidator sessions,
        IAuditService audit,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        TimeProvider time)
    {
        _clients = clients;
        _queries = queries;
        _users = users;
        _roles = roles;
        _memberships = memberships;
        _keys = keys;
        _hasher = hasher;
        _secure = secure;
        _resetService = resetService;
        _guard = guard;
        _sessions = sessions;
        _audit = audit;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<PagedResult<ClientListItemDto>>> ListAsync(ClientListQuery query, CancellationToken cancellationToken)
    {
        ClientStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<ClientStatus>(query.Status, ignoreCase: true, out var parsed))
            {
                return Error.Validation("Unknown status filter.");
            }

            status = parsed;
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _queries.ListClientsAsync(query.Search?.Trim(), status, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<ClientListItemDto>(items.Select(i => i.ToListItem()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<ClientDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var client = await _clients.GetByIdAsync(id, cancellationToken);
        return client is null || client.IsSystem ? Error.NotFound() : client.ToDto();
    }

    public async Task<Result<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken cancellationToken)
    {
        var now = Now;
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _clients.CodeExistsAsync(code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "A client with this code already exists.");
        }

        var adminKey = User.Normalize(request.AdminEmail);
        if (await _users.EmailExistsAsync(adminKey, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "A user with the admin email already exists.");
        }

        var adminRole = (await _roles.GetByNamesAsync([SystemRoles.ClientAdmin], cancellationToken)).SingleOrDefault();
        if (adminRole is null)
        {
            return Error.Failure(ErrorCodes.InternalError, "The ClientAdmin role is missing; run the migrator.");
        }

        var client = Client.Create(code, request.Name, request.ContactEmail, request.TimeZone, now);
        client.Apply(request.Name, request.LegalName, request.ContactEmail, request.ContactPhone, request.AddressLine1, request.AddressLine2,
            request.City, request.State, request.PostalCode, request.Country, request.Website, request.Industry, request.TimeZone);
        client.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        _clients.Add(client);
        await _keys.ProvisionAsync(client.Id, cancellationToken);

        // The first admin gets an unusable random password and chooses their own through the emailed invitation link.
        var admin = User.Create(request.AdminEmail, request.AdminFullName, _hasher.Hash(_secure.CreateToken()), client.Id, isPlatformUser: false, mustChangePassword: false);
        _users.Add(admin);
        await _users.SetRolesAsync(admin, [adminRole], cancellationToken);
        _memberships.Add(ClientUser.Create(client.Id, admin.Id, jobTitle: null, isOwner: true, now));
        var invitation = await _resetService.IssueAsync(admin, ResetEmailKind.Invitation, cancellationToken);

        _audit.Record(new AuditEntry("client.created", nameof(Client), client.Id.ToString(), client.Id,
            NewValues: new { client.Code, client.Name, client.ContactEmail, AdminEmail = admin.Email }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _resetService.SendAsync(admin, invitation, ResetEmailKind.Invitation, cancellationToken);
        return client.ToDto();
    }

    public async Task<Result<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken cancellationToken)
    {
        var client = await _clients.GetByIdAsync(id, cancellationToken);
        if (client is null || client.IsSystem)
        {
            return Error.NotFound();
        }

        if (!TryVersion(request.RowVersion, out var version))
        {
            return Error.Validation("rowVersion is not valid.", new Dictionary<string, string[]> { ["rowVersion"] = ["Invalid concurrency token."] });
        }

        var before = new { client.Name, client.ContactEmail, client.TimeZone, client.Country };
        _clients.SetExpectedVersion(client, version);
        client.Apply(request.Name, request.LegalName, request.ContactEmail, request.ContactPhone, request.AddressLine1, request.AddressLine2,
            request.City, request.State, request.PostalCode, request.Country, request.Website, request.Industry, request.TimeZone);
        client.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        _audit.Record(new AuditEntry("client.updated", nameof(Client), client.Id.ToString(), client.Id,
            OldValues: before, NewValues: new { client.Name, client.ContactEmail, client.TimeZone, client.Country }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return client.ToDto();
    }

    public Task<Result<ClientDto>> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, "client.activated", (c, actor, now) => c.Activate(actor, now), revokeSessions: false, cancellationToken);

    public Task<Result<ClientDto>> DeactivateAsync(Guid id, ClientStatusRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, "client.deactivated", (c, actor, now) => c.Deactivate(request.Reason, actor, now), revokeSessions: true, cancellationToken);

    public Task<Result<ClientDto>> SuspendAsync(Guid id, ClientStatusRequest request, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, "client.suspended", (c, actor, now) => c.Suspend(request.Reason ?? string.Empty, actor, now), revokeSessions: true, cancellationToken);

    public async Task<Result<PagedResult<ClientUserDto>>> ListUsersAsync(Guid id, PageRequest page, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(id, cancellationToken) is not { IsSystem: false })
        {
            return Error.NotFound();
        }

        var paging = page.Normalize();
        var (items, total) = await _queries.ListUsersAsync(id, paging.Search?.Trim(), paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<ClientUserDto>(items.Select(i => i.ToDto()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result> ResetUserPasswordAsync(Guid clientId, Guid userId, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(clientId, cancellationToken) is not { IsSystem: false })
        {
            return Error.NotFound();
        }

        var row = await _queries.GetUserAsync(clientId, userId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound();
        }

        // Support never learns or sets the password: the user gets a single-use link, and every existing session ends now.
        var user = row.User;
        var now = Now;
        user.RevokeSessions();
        var clientTokens = await _queries.GetActiveRefreshTokensAsync(clientId, cancellationToken);
        foreach (var token in clientTokens.Where(r => r.UserId == userId))
        {
            token.Revoke(now, "admin-password-reset");
        }

        var raw = await _resetService.IssueAsync(user, ResetEmailKind.Reset, cancellationToken);
        _audit.Record(new AuditEntry("client.user_password_reset", nameof(User), user.Id.ToString(), clientId));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _sessions.Invalidate(user.Id);
        await _resetService.SendAsync(user, raw, ResetEmailKind.Reset, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<PagedResult<AuditLogDto>>> GetActivityAsync(Guid id, ActivityQuery query, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(id, cancellationToken) is not { IsSystem: false })
        {
            return Error.NotFound();
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _queries.ListAuditAsync(id, query.From, query.To, query.Action, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<AuditLogDto>(items.Select(i => i.ToDto()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<PagedResult<LoginHistoryDto>>> GetLoginsAsync(Guid id, ActivityQuery query, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(id, cancellationToken) is not { IsSystem: false })
        {
            return Error.NotFound();
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _queries.ListLoginsAsync(id, query.From, query.To, query.Outcome, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<LoginHistoryDto>(items.Select(i => i.ToDto()).ToList(), paging.Page, paging.PageSize, total);
    }

    private async Task<Result<ClientDto>> ChangeStatusAsync(
        Guid id, string action, Action<Client, Guid?, DateTime> apply, bool revokeSessions, CancellationToken cancellationToken)
    {
        var client = await _clients.GetByIdAsync(id, cancellationToken);
        if (client is null || client.IsSystem)
        {
            return Error.NotFound();
        }

        var before = client.Status.ToString();
        var now = Now;
        try
        {
            apply(client, _currentUser.ActorId, now);
        }
        catch (DomainException ex)
        {
            return ex.Code == "CLIENT_SUSPEND_REASON_REQUIRED"
                ? Error.Validation(ex.Message, new Dictionary<string, string[]> { ["reason"] = [ex.Message] })
                : Error.Conflict(ex.Code, ex.Message);
        }

        if (revokeSessions)
        {
            foreach (var token in await _queries.GetActiveRefreshTokensAsync(id, cancellationToken))
            {
                token.Revoke(now, "client-" + client.Status.ToString().ToLowerInvariant());
            }
        }

        _audit.Record(new AuditEntry(action, nameof(Client), client.Id.ToString(), client.Id,
            OldValues: new { Status = before }, NewValues: new { Status = client.Status.ToString(), client.StatusReason }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _guard.Invalidate(id); // takes effect on the very next request
        return client.ToDto();
    }

    private static bool TryVersion(string value, out byte[] version)
    {
        try
        {
            version = Convert.FromBase64String(value);
            return version.Length > 0;
        }
        catch (FormatException)
        {
            version = [];
            return false;
        }
    }
}
