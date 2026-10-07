using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Identity;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Infrastructure.Persistence.Seed;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>First Super Admin, created only when no platform user exists yet. Supply via environment/secret store; the account must change its password at first login.</summary>
    public string? SuperAdminEmail { get; set; }

    public string? SuperAdminPassword { get; set; }

    public string SuperAdminName { get; set; } = "Platform Administrator";
}

/// <summary>
/// Idempotent identity seed: syncs the permission catalogue from code, keeps the built-in roles' permission sets in step with
/// the code-side defaults, and (once) creates the first Super Admin from configuration. There is no default password.
/// </summary>
public sealed class IdentitySeeder
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _policy;
    private readonly SeedOptions _options;
    private readonly ITenantScope _scope;
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(AppDbContext db, IPasswordHasher hasher, PasswordPolicy policy, IOptions<SeedOptions> options, ITenantScope scope, ILogger<IdentitySeeder> logger)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _options = options.Value;
        _scope = scope;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        using var platform = _scope.BeginPlatform("identity seed");

        await EnsureSystemClientAsync(cancellationToken);
        var permissions = await SyncPermissionsAsync(cancellationToken);
        await SyncSystemRolesAsync(permissions, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await SeedSuperAdminAsync(cancellationToken);
    }

    /// <summary>The pseudo-client that owns platform-level rows (staff accounts, platform audit events). Saved first so foreign keys resolve.</summary>
    /// <summary>
    /// Break-glass recovery: (re)creates the given account as an active Super Admin with a new password that must be changed at
    /// first sign-in. Run deliberately by an operator through the migrator; the action is audited.
    /// </summary>
    public async Task RecoverSuperAdminAsync(string email, string password, CancellationToken cancellationToken)
    {
        using var platform = _scope.BeginPlatform("super admin recovery");
        await EnsureSystemClientAsync(cancellationToken);
        var permissions = await SyncPermissionsAsync(cancellationToken);
        await SyncSystemRolesAsync(permissions, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var problems = _policy.Validate(password, email);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The recovery password does not meet the password policy: " + string.Join(" ", problems));
        }

        var role = await _db.Roles.SingleAsync(r => r.Name == SystemRoles.SuperAdmin, cancellationToken);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == User.Normalize(email), cancellationToken);
        if (user is { IsPlatformUser: false })
        {
            throw new InvalidOperationException("That email belongs to a client user and cannot be promoted.");
        }

        if (user is null)
        {
            user = User.Create(email, _options.SuperAdminName, _hasher.Hash(password), PlatformTenant.ClientId, isPlatformUser: true, mustChangePassword: true);
            _db.Users.Add(user);
        }
        else
        {
            user.SetPassword(_hasher.Hash(password), DateTime.UtcNow, mustChangePassword: true);
            user.Activate();
        }

        if (!await _db.UserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id, cancellationToken))
        {
            _db.UserRoles.Add(new UserRole { ClientId = user.ClientId, UserId = user.Id, RoleId = role.Id });
        }

        _db.AuditLogs.Add(new NexaVerify.Domain.Auditing.AuditLog
        {
            ClientId = PlatformTenant.ClientId,
            ActorType = NexaVerify.Domain.Auditing.AuditActorType.System,
            Action = "user.superadmin_recovered",
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            OccurredAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning("Super Admin {Email} was recovered; the password must be changed at first sign-in.", email);
    }

    private async Task EnsureSystemClientAsync(CancellationToken cancellationToken)
    {
        if (!await _db.Clients.AnyAsync(c => c.Id == PlatformTenant.ClientId, cancellationToken))
        {
            _db.Clients.Add(Client.CreateSystem(DateTime.UtcNow));
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Dictionary<string, Permission>> SyncPermissionsAsync(CancellationToken cancellationToken)
    {
        var existing = await _db.Permissions.ToDictionaryAsync(p => p.Key, cancellationToken);
        foreach (var definition in Permissions.All)
        {
            var scope = definition.Scope switch { PermissionScopeKind.Platform => PermissionScope.Platform, PermissionScopeKind.Client => PermissionScope.Client, _ => PermissionScope.Both };
            if (existing.TryGetValue(definition.Key, out var permission))
            {
                permission.Update(definition.Group, scope, definition.Description);
            }
            else
            {
                existing[definition.Key] = Permission.Create(definition.Key, definition.Group, scope, definition.Description);
                _db.Permissions.Add(existing[definition.Key]);
            }
        }

        return existing;
    }

    private async Task SyncSystemRolesAsync(Dictionary<string, Permission> permissions, CancellationToken cancellationToken)
    {
        var roles = await _db.Roles.Include(r => r.Permissions).Where(r => r.IsSystem).ToListAsync(cancellationToken);
        var definitions = new (string Name, RoleScope Scope, string Description)[]
        {
            (SystemRoles.SuperAdmin, RoleScope.Platform, "Full platform access"),
            (SystemRoles.ClientAdmin, RoleScope.Client, "Manages a client account"),
            (SystemRoles.ClientUser, RoleScope.Client, "Limited face-recognition access"),
        };

        foreach (var (name, scope, description) in definitions)
        {
            var role = roles.FirstOrDefault(r => r.Name == name);
            if (role is null)
            {
                role = Role.Create(name, scope, isSystem: true, description);
                _db.Roles.Add(role);
            }

            var wanted = SystemRoles.PermissionsFor(name).Select(k => permissions[k]).ToList();
            foreach (var stale in role.Permissions.Where(rp => wanted.All(p => p.Id != rp.PermissionId)).ToList())
            {
                role.RevokePermission(stale.PermissionId);
            }

            foreach (var permission in wanted)
            {
                role.GrantPermission(permission);
            }
        }
    }

    private async Task SeedSuperAdminAsync(CancellationToken cancellationToken)
    {
        if (await _db.Users.AnyAsync(u => u.IsPlatformUser, cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.SuperAdminEmail) || string.IsNullOrEmpty(_options.SuperAdminPassword))
        {
            _logger.LogWarning("No platform user exists and Seed:SuperAdminEmail / Seed:SuperAdminPassword are not set — nobody can sign in to the admin portal yet.");
            return;
        }

        var problems = _policy.Validate(_options.SuperAdminPassword, _options.SuperAdminEmail);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Seed:SuperAdminPassword does not meet the password policy: " + string.Join(" ", problems));
        }

        var user = User.Create(_options.SuperAdminEmail, _options.SuperAdminName, _hasher.Hash(_options.SuperAdminPassword),
            PlatformTenant.ClientId, isPlatformUser: true, mustChangePassword: true);
        var role = await _db.Roles.SingleAsync(r => r.Name == SystemRoles.SuperAdmin, cancellationToken);
        _db.Users.Add(user);
        _db.UserRoles.Add(new UserRole { ClientId = user.ClientId, UserId = user.Id, RoleId = role.Id });
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Created the initial Super Admin {Email}; a password change is required at first sign-in.", user.Email);
    }
}
