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

public interface IClientPortalService
{
    Task<Result<ClientDto>> GetProfileAsync(CancellationToken cancellationToken);

    Task<Result<ClientDto>> UpdateProfileAsync(UpdateClientProfileRequest request, CancellationToken cancellationToken);

    Task<Result<PagedResult<ClientUserDto>>> ListUsersAsync(PageRequest page, CancellationToken cancellationToken);

    Task<Result<ClientUserDto>> CreateUserAsync(CreateClientUserRequest request, CancellationToken cancellationToken);

    Task<Result<ClientUserDto>> UpdateUserAsync(Guid userId, UpdateClientUserRequest request, CancellationToken cancellationToken);

    Task<Result> ResetUserPasswordAsync(Guid userId, CancellationToken cancellationToken);

    Task<Result<PagedResult<AuditLogDto>>> GetActivityAsync(ActivityQuery query, CancellationToken cancellationToken);

    Task<Result<PagedResult<LoginHistoryDto>>> GetLoginsAsync(ActivityQuery query, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<SettingDto>>> GetSettingsAsync(IReadOnlyCollection<string>? groups, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(UpdateSettingsRequest request, IReadOnlyCollection<string>? groups, CancellationToken cancellationToken);
}

/// <summary>
/// Everything a client administrator does to their own account. The client is always the caller's own (taken from the
/// credential, never a parameter), enforced again by the tenant filter and row-level security.
/// </summary>
public sealed class ClientPortalService : IClientPortalService
{
    private readonly IClientRepository _clients;
    private readonly IClientQueries _queries;
    private readonly IClientService _clientService;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IClientMembershipRepository _memberships;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IClientSettingsService _settings;
    private readonly IPasswordHasher _hasher;
    private readonly ISecureTokenService _secure;
    private readonly IPasswordResetService _resetService;
    private readonly ISessionValidator _sessions;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionLock _lock;
    private readonly TimeProvider _time;

    public ClientPortalService(
        IClientRepository clients,
        IClientQueries queries,
        IClientService clientService,
        IUserRepository users,
        IRoleRepository roles,
        IClientMembershipRepository memberships,
        IRefreshTokenRepository refreshTokens,
        IClientSettingsService settings,
        IPasswordHasher hasher,
        ISecureTokenService secure,
        IPasswordResetService resetService,
        ISessionValidator sessions,
        IPermissionResolver permissions,
        IAuditService audit,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        ITransactionLock transactionLock,
        TimeProvider time)
    {
        _lock = transactionLock;
        _clients = clients;
        _queries = queries;
        _clientService = clientService;
        _users = users;
        _roles = roles;
        _memberships = memberships;
        _refreshTokens = refreshTokens;
        _settings = settings;
        _hasher = hasher;
        _secure = secure;
        _resetService = resetService;
        _sessions = sessions;
        _permissions = permissions;
        _audit = audit;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private Guid? OwnClientId => _currentUser.ClientId;

    public async Task<Result<ClientDto>> GetProfileAsync(CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } id)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users have a client profile.");
        }

        var client = await _clients.GetByIdAsync(id, cancellationToken);
        return client is null ? Error.NotFound() : client.ToClientView();
    }

    public async Task<Result<ClientDto>> UpdateProfileAsync(UpdateClientProfileRequest request, CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } id)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users have a client profile.");
        }

        var client = await _clients.GetByIdAsync(id, cancellationToken);
        if (client is null)
        {
            return Error.NotFound();
        }

        byte[] version;
        try
        {
            version = Convert.FromBase64String(request.RowVersion);
        }
        catch (FormatException)
        {
            return Error.Validation("rowVersion is not valid.", new Dictionary<string, string[]> { ["rowVersion"] = ["Invalid concurrency token."] });
        }

        var before = new { client.Name, client.ContactEmail, client.TimeZone };
        _clients.SetExpectedVersion(client, version);
        client.Apply(request.Name, request.LegalName, request.ContactEmail, request.ContactPhone, request.AddressLine1, request.AddressLine2,
            request.City, request.State, request.PostalCode, request.Country, request.Website, request.Industry, request.TimeZone);
        _audit.Record(new AuditEntry("client.profile_updated", nameof(Client), client.Id.ToString(), client.Id,
            OldValues: before, NewValues: new { client.Name, client.ContactEmail, client.TimeZone }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return client.ToClientView();
    }

    public async Task<Result<PagedResult<ClientUserDto>>> ListUsersAsync(PageRequest page, CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } id)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users can list users.");
        }

        var paging = page.Normalize();
        var (items, total) = await _queries.ListUsersAsync(id, paging.Search?.Trim(), paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<ClientUserDto>(items.Select(i => i.ToDto()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<ClientUserDto>> CreateUserAsync(CreateClientUserRequest request, CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users can create users.");
        }

        var settings = await _settings.GetEffectiveAsync(clientId, cancellationToken);
        var maxUsers = settings.Int(SettingKeys.Limits.MaxUsers);
        if (await _queries.CountActiveUsersAsync(clientId, cancellationToken) >= maxUsers)
        {
            return Error.Conflict("USER_LIMIT_REACHED", "The maximum number of users for this account has been reached.");
        }

        var normalized = User.Normalize(request.Email);
        if (await _users.EmailExistsAsync(normalized, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "A user with this email already exists.");
        }

        var role = await ResolveRoleAsync(request.Role, cancellationToken);
        if (role.IsFailure)
        {
            return role.Error!;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var user = User.Create(request.Email, request.FullName, _hasher.Hash(_secure.CreateToken()), clientId, isPlatformUser: false, mustChangePassword: false);
        string invitation;
        try
        {
            // The early count above is only a quick answer; the authoritative check runs under a per-client lock inside the transaction.
            invitation = await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    await _lock.AcquireAsync("users:" + clientId.ToString("N"), ct);
                    if (await _queries.CountActiveUsersAsync(clientId, ct) >= maxUsers)
                    {
                        throw new CapExceededException("USER_LIMIT_REACHED", "The maximum number of users for this account has been reached.");
                    }

                    _users.Add(user);
                    await _users.SetRolesAsync(user, [role.Value], ct);
                    _memberships.Add(ClientUser.Create(clientId, user.Id, Clean(request.JobTitle), isOwner: false, now));
                    var issued = await _resetService.IssueAsync(user, ResetEmailKind.Invitation, ct);
                    _audit.Record(new AuditEntry(AuditActions.UserCreated, nameof(User), user.Id.ToString(), clientId,
                        NewValues: new { user.Email, user.FullName, Role = role.Value.Name }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return issued;
                },
                cancellationToken);
        }
        catch (CapExceededException ex)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ex.Code, ex.Message);
        }

        await _resetService.SendAsync(user, invitation, ResetEmailKind.Invitation, cancellationToken);

        var row = await _queries.GetUserAsync(clientId, user.Id, cancellationToken);
        return row!.ToDto();
    }

    public async Task<Result<ClientUserDto>> UpdateUserAsync(Guid userId, UpdateClientUserRequest request, CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users can update users.");
        }

        var row = await _queries.GetUserAsync(clientId, userId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound();
        }

        var role = await ResolveRoleAsync(request.Role, cancellationToken);
        if (role.IsFailure)
        {
            return role.Error!;
        }

        if (await CheckTargetPrivilegeAsync(row, cancellationToken) is { } denied)
        {
            return denied;
        }

        var user = row.User;
        if (user.Id == _currentUser.ActorId && (!request.IsActive || role.Value.Name != row.Role))
        {
            return Error.Conflict(ErrorCodes.Conflict, !request.IsActive ? "You cannot deactivate your own account." : "You cannot change your own role.");
        }

        var losesAdmin = row.Role == SystemRoles.ClientAdmin && (role.Value.Name != SystemRoles.ClientAdmin || !request.IsActive);
        var before = new { user.FullName, Status = user.Status.ToString(), Role = row.Role };
        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    if (losesAdmin)
                    {
                        // Two admins demoting each other in parallel must not leave the account without one: serialise the check.
                        await _lock.AcquireAsync("admins:" + clientId.ToString("N"), ct);
                        if (await _queries.CountActiveUsersWithRoleAsync(clientId, SystemRoles.ClientAdmin, ct) <= 1)
                        {
                            throw new CapExceededException("LAST_ADMIN", "The account must keep at least one active administrator.");
                        }
                    }

                    user.FullName = request.FullName.Trim();
                    if (request.IsActive)
                    {
                        user.Activate();
                    }
                    else
                    {
                        user.Deactivate();
                    }

                    await _users.SetRolesAsync(user, [role.Value], ct);
                    user.RevokeSessions(); // a changed role or status applies to already-issued tokens right away
                    if (!request.IsActive)
                    {
                        var now = _time.GetUtcNow().UtcDateTime;
                        foreach (var token in await _refreshTokens.GetActiveForUserAsync(user.Id, ct))
                        {
                            token.Revoke(now, "user-deactivated");
                        }
                    }

                    if (row.Membership is not null)
                    {
                        row.Membership.JobTitle = Clean(request.JobTitle);
                    }

                    _audit.Record(new AuditEntry(request.IsActive ? AuditActions.UserUpdated : AuditActions.UserDeactivated, nameof(User), user.Id.ToString(), clientId,
                        OldValues: before, NewValues: new { user.FullName, Status = user.Status.ToString(), Role = role.Value.Name }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);
        }
        catch (CapExceededException ex)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ex.Code, ex.Message);
        }

        _sessions.Invalidate(user.Id);

        var updated = await _queries.GetUserAsync(clientId, userId, cancellationToken);
        return updated!.ToDto();
    }

    public async Task<Result> ResetUserPasswordAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users can reset passwords.");
        }

        var row = await _queries.GetUserAsync(clientId, userId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound();
        }

        // Resetting a password mails a link and ends the user's sessions: that is a modification, so the same privilege rule applies.
        return await CheckTargetPrivilegeAsync(row, cancellationToken) is { } denied
            ? denied
            : await _clientService.ResetUserPasswordAsync(clientId, userId, cancellationToken);
    }

    public async Task<Result<PagedResult<AuditLogDto>>> GetActivityAsync(ActivityQuery query, CancellationToken cancellationToken)
    {
        if (OwnClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client users have an activity history.");
        }

        var page = await _clientService.GetActivityAsync(clientId, query, cancellationToken);
        if (page.IsFailure)
        {
            return page;
        }

        // Rows written by platform staff (suspensions, license changes, password resets...) carry the staff member's id and the IP
        // address they worked from. Those belong to the platform, not to the client: show "Platform" and nothing else about the person.
        var userActors = page.Value.Items
            .Where(a => a is { ActorType: nameof(Domain.Auditing.AuditActorType.User), ActorId: not null })
            .Select(a => a.ActorId!.Value).Distinct().ToList();
        var own = userActors.Count == 0 ? [] : await _queries.GetUserIdsAsync(clientId, userActors, cancellationToken);
        var items = page.Value.Items
            .Select(a => a is { ActorType: nameof(Domain.Auditing.AuditActorType.User), ActorId: { } actor } && !own.Contains(actor)
                ? a with { ActorType = PlatformActorLabel, ActorId = null, IpAddress = null }
                : a)
            .ToList();
        return new PagedResult<AuditLogDto>(items, page.Value.Page, page.Value.PageSize, page.Value.TotalCount);
    }

    /// <summary>What client views show instead of a platform staff member.</summary>
    public const string PlatformActorLabel = "Platform";

    public async Task<Result<PagedResult<LoginHistoryDto>>> GetLoginsAsync(ActivityQuery query, CancellationToken cancellationToken) =>
        OwnClientId is { } clientId
            ? await _clientService.GetLoginsAsync(clientId, query, cancellationToken)
            : Error.Forbidden(ErrorCodes.Forbidden, "Only client users have a login history.");

    public async Task<Result<IReadOnlyList<SettingDto>>> GetSettingsAsync(IReadOnlyCollection<string>? groups, CancellationToken cancellationToken) =>
        OwnClientId is { } clientId
            ? await _settings.GetAsync(clientId, groups, cancellationToken)
            : Error.Forbidden(ErrorCodes.Forbidden, "Only client users have settings.");

    public async Task<Result<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(UpdateSettingsRequest request, IReadOnlyCollection<string>? groups, CancellationToken cancellationToken) =>
        OwnClientId is { } clientId
            ? await _settings.UpdateAsync(clientId, request, groups, cancellationToken)
            : Error.Forbidden(ErrorCodes.Forbidden, "Only client users have settings.");

    private async Task<Result<Role>> ResolveRoleAsync(string name, CancellationToken cancellationToken)
    {
        var role = (await _roles.GetByNamesAsync([name], cancellationToken)).SingleOrDefault();
        if (role is null || role.Scope != RoleScope.Client)
        {
            return Error.Validation("Unknown role.", new Dictionary<string, string[]> { ["role"] = ["Choose one of the available client roles."] });
        }

        // You cannot hand out more than you hold.
        var mine = await _permissions.GetPermissionsAsync(_currentUser.Roles, CancellationToken.None);
        var catalogue = (await _roles.ListPermissionsAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Key);
        var escalation = role.Permissions.Select(p => catalogue[p.PermissionId]).Where(k => !mine.Contains(k)).ToList();
        return escalation.Count > 0
            ? Error.Forbidden(ErrorCodes.Forbidden, "You cannot assign a role with permissions you do not hold.")
            : role;
    }

    /// <summary>
    /// A user manager may only modify someone with strictly less privilege than themselves (every permission of the target is held by
    /// the caller and the sets differ), never the account owner, and never an equal - except that the account owner may manage equals.
    /// Editing yourself is allowed here (what you may change about yourself is limited by the callers).
    /// </summary>
    private async Task<Error?> CheckTargetPrivilegeAsync(ClientUserRow target, CancellationToken cancellationToken)
    {
        if (target.User.Id == _currentUser.ActorId)
        {
            return null;
        }

        var mine = await _permissions.GetPermissionsAsync(_currentUser.Roles, CancellationToken.None);
        var theirs = await _permissions.GetPermissionsAsync([target.Role], CancellationToken.None);
        var callerIsOwner = _currentUser.ActorId is { } actor && (await _memberships.GetByUserIdAsync(actor, cancellationToken))?.IsOwner == true;
        var targetIsOwner = target.Membership?.IsOwner == true;

        var lowerOrEqual = theirs.IsSubsetOf(mine);
        var equal = lowerOrEqual && theirs.SetEquals(mine);
        if (!lowerOrEqual || targetIsOwner || (equal && !callerIsOwner))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "You cannot change a user whose access is equal to or higher than yours.");
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
