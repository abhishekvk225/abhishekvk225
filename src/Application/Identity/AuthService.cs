using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<Result<LoginResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);

    Task<Result> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken);

    Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);

    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);

    Task<Result<LoginResponse>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken);

    Task<Result<MeResponse>> GetMeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sign-in, token rotation and password flows. Pre-authentication lookups (the tenant is not known until the user is found)
/// run in a narrowly scoped, reasoned platform scope. Failure responses never reveal whether an account exists.
/// </summary>
public sealed class AuthService : IAuthService
{
    private const string GenericLoginFailure = "Invalid email or password.";
    private const string SessionInvalid = "The session is no longer valid. Please sign in again.";
    private const string RotatedReason = "rotated";

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordResetTokenRepository _resetTokens;
    private readonly ILoginHistoryRepository _history;
    private readonly IPermissionResolver _permissions;
    private readonly IPasswordHasher _hasher;
    private readonly ISecureTokenService _tokens;
    private readonly IAccessTokenIssuer _accessTokens;
    private readonly IEmailSender _email;
    private readonly IClientAccessGuard _clientGuard;
    private readonly IAuditService _audit;
    private readonly IRequestInfo _request;
    private readonly ICurrentUser _currentUser;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantScope _scope;
    private readonly ISessionValidator _sessions;
    private readonly TimeProvider _time;
    private readonly AuthOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordResetTokenRepository resetTokens,
        ILoginHistoryRepository history,
        IPermissionResolver permissions,
        IPasswordHasher hasher,
        ISecureTokenService tokens,
        IAccessTokenIssuer accessTokens,
        IEmailSender email,
        IClientAccessGuard clientGuard,
        IAuditService audit,
        IRequestInfo request,
        ICurrentUser currentUser,
        IUnitOfWork unitOfWork,
        ITenantScope scope,
        ISessionValidator sessions,
        TimeProvider time,
        IOptions<AuthOptions> options,
        ILogger<AuthService> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _resetTokens = resetTokens;
        _history = history;
        _permissions = permissions;
        _hasher = hasher;
        _tokens = tokens;
        _accessTokens = accessTokens;
        _email = email;
        _clientGuard = clientGuard;
        _audit = audit;
        _request = request;
        _currentUser = currentUser;
        _unitOfWork = unitOfWork;
        _scope = scope;
        _sessions = sessions;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scope.BeginPlatform("authentication lookup");
        var now = Now;
        var user = await _users.GetByNormalizedEmailAsync(User.Normalize(request.Email), cancellationToken);

        if (user is null)
        {
            _hasher.BurnTime(request.Password);
            RecordLogin(null, PlatformTenant.ClientId, request.Email, LoginOutcome.InvalidCredentials, "unknown email");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, GenericLoginFailure);
        }

        if (user.IsLockedOut(now))
        {
            _hasher.BurnTime(request.Password);
            RecordLogin(user, user.ClientId, request.Email, LoginOutcome.LockedOut, "account locked");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, GenericLoginFailure);
        }

        var verification = _hasher.Verify(user.PasswordHash, request.Password);
        if (verification == PasswordVerification.Failed)
        {
            var locked = user.RegisterFailedLogin(now, _options.MaxFailedAttempts, TimeSpan.FromMinutes(_options.LockoutMinutes));
            RecordLogin(user, user.ClientId, request.Email, locked ? LoginOutcome.LockedOut : LoginOutcome.InvalidCredentials, locked ? "locked after repeated failures" : "bad password");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, GenericLoginFailure);
        }

        // From here the caller proved they own the account, so specific reasons are safe to return.
        if (!user.CanSignIn(now))
        {
            RecordLogin(user, user.ClientId, request.Email, LoginOutcome.Inactive, "account inactive");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Error.Forbidden(ErrorCodes.Forbidden, "This account is not active. Contact your administrator.");
        }

        if (!user.IsPlatformUser && await _clientGuard.CheckAsync(user.ClientId, cancellationToken) is { } blocked)
        {
            RecordLogin(user, user.ClientId, request.Email, LoginOutcome.ClientSuspended, blocked.Code);
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return blocked;
        }

        user.RegisterSuccessfulLogin(now);
        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.SetPassword(_hasher.Hash(request.Password), now, user.MustChangePassword);
            _sessions.Invalidate(user.Id);
        }

        var response = await IssueSessionAsync(user, familyId: null, now, cancellationToken);
        RecordLogin(user, user.ClientId, request.Email, LoginOutcome.Success, null);
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return response;
    }

    public async Task<Result<LoginResponse>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scope.BeginPlatform("refresh token lookup");
        var now = Now;
        var token = await _refreshTokens.FindByHashAsync(_tokens.Hash(request.RefreshToken), cancellationToken);
        if (token is null)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        if (token.IsRevoked)
        {
            // A rotated token being presented again means it leaked: kill the whole family.
            if (token.RevokedReason == RotatedReason)
            {
                foreach (var sibling in await _refreshTokens.GetFamilyAsync(token.FamilyId, cancellationToken))
                {
                    sibling.Revoke(now, "reuse-detected");
                }

                _audit.Record(new AuditEntry(AuditActions.SessionReuseDetected, nameof(RefreshToken), token.FamilyId.ToString(), token.ClientId, ActorId: token.UserId));
                await _unitOfWork.SaveChangesAsync(CancellationToken.None);
                _logger.LogWarning("Refresh token reuse detected for family {FamilyId}", token.FamilyId);
            }

            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        if (token.IsExpired(now))
        {
            return Error.Unauthenticated(ErrorCodes.TokenExpired, SessionInvalid);
        }

        var user = await _users.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null || !user.CanSignIn(now))
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        if (!user.IsPlatformUser && await _clientGuard.CheckAsync(user.ClientId, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var response = await IssueSessionAsync(user, token.FamilyId, now, cancellationToken, replacing: token);
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return response;
    }

    public async Task<Result> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } userId || _currentUser.ActorType != ActorType.User)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        var now = Now;
        if (!string.IsNullOrEmpty(request.RefreshToken))
        {
            var token = await _refreshTokens.FindByHashAsync(_tokens.Hash(request.RefreshToken), cancellationToken);
            if (token is not null && token.UserId == userId)
            {
                foreach (var sibling in await _refreshTokens.GetFamilyAsync(token.FamilyId, cancellationToken))
                {
                    sibling.Revoke(now, "logout");
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scope.BeginPlatform("password reset lookup");
        var now = Now;
        var user = await _users.GetByNormalizedEmailAsync(User.Normalize(request.Email), cancellationToken);

        // Same observable behaviour whether or not the account exists (the response is always success).
        if (user is null || !user.CanSignIn(now))
        {
            _hasher.BurnTime(request.Email);
            return Result.Success();
        }

        foreach (var old in await _resetTokens.GetUnusedForUserAsync(user.Id, now, cancellationToken))
        {
            old.MarkUsed(now);
        }

        var raw = _tokens.CreateToken();
        _resetTokens.Add(PasswordResetToken.Issue(user, _tokens.Hash(raw), now, TimeSpan.FromMinutes(_options.PasswordResetMinutes)));
        _audit.Record(new AuditEntry(AuditActions.PasswordResetRequested, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);

        var link = _options.PasswordResetUrlTemplate
            .Replace("{email}", Uri.EscapeDataString(user.Email), StringComparison.Ordinal)
            .Replace("{token}", Uri.EscapeDataString(raw), StringComparison.Ordinal);
        try
        {
            await _email.SendAsync(
                new EmailMessage(
                    user.Email,
                    "Reset your NexaVerify password",
                    $"Hello {user.FullName},\n\nUse the link below to choose a new password. It expires in {_options.PasswordResetMinutes} minutes and can be used once.\n\n{link}\n\nIf you did not request this, you can ignore this email."),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let mail problems change the (constant) response; the failure is operational, not the caller's.
            _logger.LogError(ex, "Failed to send password reset email for user {UserId}", user.Id);
        }

        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scope.BeginPlatform("password reset");
        var now = Now;
        var invalid = Error.Validation("The reset link is invalid or has expired.");
        var user = await _users.GetByNormalizedEmailAsync(User.Normalize(request.Email), cancellationToken);
        if (user is null || !user.CanSignIn(now))
        {
            return invalid;
        }

        var token = await _resetTokens.FindUsableAsync(user.Id, _tokens.Hash(request.Token), now, cancellationToken);
        if (token is null)
        {
            RecordLogin(user, user.ClientId, request.Email, LoginOutcome.InvalidCredentials, "bad reset token");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return invalid;
        }

        token.MarkUsed(now);
        user.SetPassword(_hasher.Hash(request.NewPassword), now, mustChangePassword: false);
        foreach (var refresh in await _refreshTokens.GetActiveForUserAsync(user.Id, cancellationToken))
        {
            refresh.Revoke(now, "password-reset");
        }

        RecordLogin(user, user.ClientId, request.Email, LoginOutcome.PasswordReset, null);
        _audit.Record(new AuditEntry(AuditActions.PasswordReset, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        _sessions.Invalidate(user.Id);
        return Result.Success();
    }

    public async Task<Result<LoginResponse>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } userId || _currentUser.ActorType != ActorType.User)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        var now = Now;
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Error.NotFound();
        }

        if (_hasher.Verify(user.PasswordHash, request.CurrentPassword) == PasswordVerification.Failed)
        {
            var locked = user.RegisterFailedLogin(now, _options.MaxFailedAttempts, TimeSpan.FromMinutes(_options.LockoutMinutes));
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Error.Validation(
                locked ? "Too many failed attempts. The account is temporarily locked." : "The current password is incorrect.",
                new Dictionary<string, string[]> { ["currentPassword"] = ["The current password is incorrect."] });
        }

        user.SetPassword(_hasher.Hash(request.NewPassword), now, mustChangePassword: false);
        foreach (var refresh in await _refreshTokens.GetActiveForUserAsync(user.Id, cancellationToken))
        {
            refresh.Revoke(now, "password-changed");
        }

        var response = await IssueSessionAsync(user, familyId: null, now, cancellationToken);
        _audit.Record(new AuditEntry(AuditActions.PasswordChanged, nameof(User), user.Id.ToString(), user.ClientId));
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        _sessions.Invalidate(user.Id);
        return response;
    }

    public async Task<Result<MeResponse>> GetMeAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } userId)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);
        }

        var roles = await _users.GetRoleNamesAsync(user.Id, cancellationToken);
        var permissions = await _permissions.GetPermissionsAsync(roles, cancellationToken);
        return new MeResponse(user.ToSummary(roles), permissions.OrderBy(p => p, StringComparer.Ordinal).ToList(), user.MustChangePassword);
    }

    private async Task<LoginResponse> IssueSessionAsync(User user, Guid? familyId, DateTime now, CancellationToken cancellationToken, RefreshToken? replacing = null)
    {
        var roles = await _users.GetRoleNamesAsync(user.Id, cancellationToken);
        var access = _accessTokens.Issue(user, roles);

        var absolute = replacing?.AbsoluteExpiresAt ?? now.AddDays(_options.RefreshTokenAbsoluteDays);
        var raw = _tokens.CreateToken();
        var refresh = RefreshToken.Issue(
            user,
            _tokens.Hash(raw),
            familyId ?? Guid.CreateVersion7(),
            now,
            TimeSpan.FromDays(_options.RefreshTokenSlidingDays),
            absolute,
            _request.IpAddress,
            _request.UserAgent);
        _refreshTokens.Add(refresh);
        replacing?.Revoke(now, RotatedReason, refresh.Id);

        return new LoginResponse(access.Value, "Bearer", access.ExpiresInSeconds, raw, user.MustChangePassword, user.ToSummary(roles));
    }

    private void RecordLogin(User? user, Guid clientId, string emailAttempted, LoginOutcome outcome, string? reason)
    {
        _history.Add(new LoginHistory
        {
            ClientId = clientId,
            UserId = user?.Id,
            EmailAttempted = emailAttempted.Length > 256 ? emailAttempted[..256] : emailAttempted,
            Outcome = outcome,
            FailureReason = reason,
            IpAddress = _request.IpAddress,
            UserAgent = _request.UserAgent is { Length: > 300 } ua ? ua[..300] : _request.UserAgent,
            CorrelationId = _request.CorrelationId,
            OccurredAt = Now,
        });
    }
}
