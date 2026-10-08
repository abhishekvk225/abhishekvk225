using System.Diagnostics;
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

    private static readonly Error InvalidCredentials = Error.Unauthenticated(ErrorCodes.Unauthenticated, GenericLoginFailure);
    private static readonly Error SessionInvalidError = Error.Unauthenticated(ErrorCodes.Unauthenticated, SessionInvalid);

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRefreshTokenClaimer _claimer;
    private readonly IPasswordResetTokenRepository _resetTokens;
    private readonly ILoginHistoryRepository _history;
    private readonly ILoginThrottle _throttle;
    private readonly IPermissionResolver _permissions;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _policy;
    private readonly ISecureTokenService _tokens;
    private readonly ISessionIssuer _sessionIssuer;
    private readonly IMfaService _mfa;
    private readonly IMfaPolicy _mfaPolicy;
    private readonly IPasswordResetService _resetService;
    private readonly IClientRepository _clients;
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
        IRefreshTokenClaimer claimer,
        IPasswordResetTokenRepository resetTokens,
        ILoginHistoryRepository history,
        ILoginThrottle throttle,
        IPermissionResolver permissions,
        IPasswordHasher hasher,
        PasswordPolicy policy,
        ISecureTokenService tokens,
        ISessionIssuer sessionIssuer,
        IMfaService mfa,
        IMfaPolicy mfaPolicy,
        IPasswordResetService resetService,
        IClientRepository clients,
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
        _claimer = claimer;
        _resetTokens = resetTokens;
        _history = history;
        _throttle = throttle;
        _permissions = permissions;
        _hasher = hasher;
        _policy = policy;
        _tokens = tokens;
        _sessionIssuer = sessionIssuer;
        _mfa = mfa;
        _mfaPolicy = mfaPolicy;
        _resetService = resetService;
        _clients = clients;
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

    private TimeSpan LockoutDuration => TimeSpan.FromMinutes(_options.LockoutMinutes);

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scope.BeginPlatform("authentication lookup");
        var now = Now;
        var user = await _users.GetByNormalizedEmailAsync(User.Normalize(request.Email), cancellationToken);

        if (user is null)
        {
            _hasher.BurnTime(request.Password);
            return await FailLoginAsync(null, PlatformTenant.ClientId, request.Email, LoginOutcome.InvalidCredentials, "unknown email", InvalidCredentials);
        }

        if (user.IsLockedOut(now))
        {
            _hasher.BurnTime(request.Password);
            return await FailLoginAsync(user, user.ClientId, request.Email, LoginOutcome.LockedOut, "account locked", InvalidCredentials);
        }

        // Reserve the attempt BEFORE checking the password: parallel guesses each consume an attempt, so a burst cannot out-run the lockout.
        var attempt = await _throttle.ReserveAttemptAsync(user.Id, now, _options.MaxFailedAttempts, LockoutDuration, cancellationToken);
        if (attempt.Locked)
        {
            _hasher.BurnTime(request.Password);
            return await FailLoginAsync(user, user.ClientId, request.Email, LoginOutcome.LockedOut, "locked after repeated failures", InvalidCredentials);
        }

        var verification = _hasher.Verify(user.PasswordHash, request.Password);
        if (verification == PasswordVerification.Failed)
        {
            return await FailLoginAsync(user, user.ClientId, request.Email, LoginOutcome.InvalidCredentials, "bad password", InvalidCredentials);
        }

        // From here the caller proved they own the account, so specific reasons are safe to return.
        if (!user.CanSignIn())
        {
            return await FailLoginAsync(user, user.ClientId, request.Email, LoginOutcome.Inactive, "account inactive",
                Error.Forbidden(ErrorCodes.Forbidden, "This account is not active. Contact your administrator."));
        }

        if (!user.IsPlatformUser && await _clientGuard.CheckAsync(user.ClientId, cancellationToken) is { } blocked)
        {
            return await FailLoginAsync(user, user.ClientId, request.Email, LoginOutcome.ClientSuspended, blocked.Code, blocked);
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.UpgradeHash(_hasher.Hash(request.Password)); // same password, stronger hash: other sessions stay valid
        }

        if (user.TwoFactorEnabled)
        {
            // Password proven, second factor pending. The failed-attempt counter is deliberately NOT cleared here (only a finished
            // second factor clears it), so repeating "log in, guess a code" cannot out-run the lockout.
            var ticket = await _mfa.StartChallengeAsync(user, now, cancellationToken);
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return new LoginResponse(string.Empty, "Bearer", 0, string.Empty, false, user.ToSummary([]), MfaRequired: true,
                MfaChallengeToken: ticket.Token, MfaChallengeExpiresIn: ticket.ExpiresInSeconds);
        }

        await _throttle.ClearAsync(user.Id, cancellationToken);
        user.RegisterSuccessfulLogin(now);
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
            return SessionInvalidError;
        }

        if (token.IsRevoked)
        {
            // Replaying a rotated token well after rotation means it leaked: kill the whole family.
            // Within the grace window it is just a double-submit (two tabs refreshing at once) and is merely refused.
            var withinGrace = token.RevokedAt is { } revokedAt && now - revokedAt <= TimeSpan.FromSeconds(_options.RefreshReuseGraceSeconds);
            if (token.RevokedReason == RotatedReason && !withinGrace)
            {
                await RevokeFamilyAsync(token, now, cancellationToken);
            }

            return SessionInvalidError;
        }

        if (token.IsExpired(now))
        {
            return Error.Unauthenticated(ErrorCodes.TokenExpired, SessionInvalid);
        }

        var user = await _users.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null || !user.CanSignIn())
        {
            return SessionInvalidError;
        }

        if (!user.IsPlatformUser && await _clientGuard.CheckAsync(user.ClientId, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        // Exactly one concurrent caller wins the right to rotate this token; the others are refused (and do not fork the family).
        if (!await _claimer.TryClaimAsync(token.Id, now, cancellationToken))
        {
            return SessionInvalidError;
        }

        var response = await IssueSessionAsync(user, token.FamilyId, now, cancellationToken, replacing: token);
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return response;
    }

    public async Task<Result> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } userId || _currentUser.ActorType != ActorType.User)
        {
            return SessionInvalidError;
        }

        if (!string.IsNullOrEmpty(request.RefreshToken))
        {
            var token = await _refreshTokens.FindByHashAsync(_tokens.Hash(request.RefreshToken), cancellationToken);
            if (token is not null && token.UserId == userId)
            {
                var now = Now;
                foreach (var sibling in await _refreshTokens.GetFamilyAsync(token.FamilyId, cancellationToken))
                {
                    sibling.Revoke(now, "logout");
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return Result.Success();
    }

    public async Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var scope = _scope.BeginPlatform("password reset lookup");
            var now = Now;
            var user = await _users.GetByNormalizedEmailAsync(User.Normalize(request.Email), cancellationToken);

            // Locked accounts may still reset (the reset is the way out); inactive ones may not. Cooldown blocks email-bombing.
            if (user is not null && user.CanSignIn()
                && !await RecentResetExistsAsync(user.Id, now, cancellationToken))
            {
                var raw = await _resetService.IssueAsync(user, ResetEmailKind.Reset, cancellationToken);
                _audit.Record(new AuditEntry(AuditActions.PasswordResetRequested, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
                await _unitOfWork.SaveChangesAsync(CancellationToken.None);
                await _resetService.SendAsync(user, raw, ResetEmailKind.Reset, cancellationToken);
            }

            return Result.Success();
        }
        finally
        {
            await PadAsync(timer, cancellationToken);
        }
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var scope = _scope.BeginPlatform("password reset");
            var now = Now;
            var invalid = Error.Validation("The reset link is invalid or has expired.");
            var user = await _users.GetByNormalizedEmailAsync(User.Normalize(request.Email), cancellationToken);
            if (user is null || !user.CanSignIn())
            {
                return invalid;
            }

            var token = await _resetTokens.FindUsableAsync(user.Id, _tokens.Hash(request.Token), now, cancellationToken);
            if (token is null)
            {
                return invalid; // deliberately not recorded: anonymous callers must not be able to write rows into a victim's history
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
            await _throttle.ClearAsync(user.Id, cancellationToken); // the emailed link is the way out of a lockout
            _sessions.Invalidate(user.Id);
            return Result.Success();
        }
        finally
        {
            await PadAsync(timer, cancellationToken);
        }
    }

    public async Task<Result<LoginResponse>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } userId || _currentUser.ActorType != ActorType.User)
        {
            return SessionInvalidError;
        }

        var now = Now;
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Error.NotFound();
        }

        // A stolen access token must not become an unthrottled oracle for the current password: same lockout as sign-in.
        var attempt = user.IsLockedOut(now)
            ? new AttemptState(true, 0, user.LockoutEnd)
            : await _throttle.ReserveAttemptAsync(user.Id, now, _options.MaxFailedAttempts, LockoutDuration, cancellationToken);
        if (attempt.Locked)
        {
            return Error.Validation("Too many failed attempts. The account is temporarily locked.");
        }

        if (_hasher.Verify(user.PasswordHash, request.CurrentPassword) == PasswordVerification.Failed)
        {
            return Error.Validation("The current password is incorrect.", new Dictionary<string, string[]> { ["currentPassword"] = ["The current password is incorrect."] });
        }

        var problems = _policy.Validate(request.NewPassword, user.Email);
        if (problems.Count > 0)
        {
            return Error.Validation("The new password is not acceptable.", new Dictionary<string, string[]> { ["newPassword"] = [.. problems] });
        }

        user.SetPassword(_hasher.Hash(request.NewPassword), now, mustChangePassword: false);
        foreach (var refresh in await _refreshTokens.GetActiveForUserAsync(user.Id, cancellationToken))
        {
            refresh.Revoke(now, "password-changed");
        }

        foreach (var pending in await _resetTokens.GetUnusedForUserAsync(user.Id, now, cancellationToken))
        {
            pending.MarkUsed(now); // an old reset link must not outlive a password change
        }

        var response = await IssueSessionAsync(user, familyId: null, now, cancellationToken);
        _audit.Record(new AuditEntry(AuditActions.PasswordChanged, nameof(User), user.Id.ToString(), user.ClientId));
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        await _throttle.ClearAsync(user.Id, cancellationToken);
        _sessions.Invalidate(user.Id);
        return response;
    }

    public async Task<Result<MeResponse>> GetMeAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } userId)
        {
            return SessionInvalidError;
        }

        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return SessionInvalidError;
        }

        var roles = await _users.GetRoleNamesAsync(user.Id, cancellationToken);
        var permissions = await _permissions.GetPermissionsAsync(roles, cancellationToken);
        var client = user.IsPlatformUser ? null : await _clients.GetByIdAsync(user.ClientId, cancellationToken);
        return new MeResponse(
            user.ToSummary(roles),
            permissions.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            user.MustChangePassword,
            client is null ? null : new Contracts.Tenancy.ClientSummary(client.Id, client.Code, client.Name, client.Status.ToString(), client.TimeZone),
            user.TwoFactorEnabled,
            await _mfaPolicy.EnrolmentRequiredAsync(user, roles, cancellationToken));
    }

    private async Task<bool> RecentResetExistsAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        if (_options.PasswordResetCooldownSeconds <= 0)
        {
            return false;
        }

        var cutoff = now - TimeSpan.FromSeconds(_options.PasswordResetCooldownSeconds);
        return (await _resetTokens.GetUnusedForUserAsync(userId, now, cancellationToken)).Any(t => t.CreatedAt > cutoff);
    }

    /// <summary>Makes sensitive responses take at least a fixed time so latency cannot be used to tell existing accounts apart.</summary>
    private async Task PadAsync(Stopwatch timer, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMilliseconds(_options.SensitiveResponseMinimumMilliseconds) - timer.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private async Task RevokeFamilyAsync(RefreshToken token, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var sibling in await _refreshTokens.GetFamilyAsync(token.FamilyId, cancellationToken))
        {
            sibling.Revoke(now, "reuse-detected");
        }

        _audit.Record(new AuditEntry(AuditActions.SessionReuseDetected, nameof(RefreshToken), token.FamilyId.ToString(), token.ClientId, ActorId: token.UserId));
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        _logger.LogWarning("Refresh token reuse detected for family {FamilyId}", token.FamilyId);
    }

    private Task<LoginResponse> IssueSessionAsync(User user, Guid? familyId, DateTime now, CancellationToken cancellationToken, RefreshToken? replacing = null) =>
        _sessionIssuer.IssueAsync(user, familyId, now, cancellationToken, replacing);

    private async Task<Result<LoginResponse>> FailLoginAsync(User? user, Guid clientId, string emailAttempted, LoginOutcome outcome, string? reason, Error error)
    {
        RecordLogin(user, clientId, emailAttempted, outcome, reason);
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return error;
    }

    private void RecordLogin(User? user, Guid clientId, string emailAttempted, LoginOutcome outcome, string? reason)
    {
        // Unknown "emails" are often passwords pasted into the wrong box: never store them verbatim, keep only a short fingerprint.
        var recorded = user is not null
            ? (emailAttempted.Length > User.EmailMaxLength ? emailAttempted[..User.EmailMaxLength] : emailAttempted)
            : "unknown:" + Convert.ToHexString(_tokens.Hash(emailAttempted.Trim().ToLowerInvariant()))[..12];

        _history.Add(new LoginHistory
        {
            ClientId = clientId,
            UserId = user?.Id,
            EmailAttempted = recorded,
            Outcome = outcome,
            FailureReason = reason,
            IpAddress = _request.IpAddress,
            UserAgent = Truncate(_request.UserAgent, 300),
            CorrelationId = _request.CorrelationId,
            OccurredAt = Now,
        });
    }

    private static string? Truncate(string? value, int max) => value is { Length: var length } && length > max ? value[..max] : value;
}
