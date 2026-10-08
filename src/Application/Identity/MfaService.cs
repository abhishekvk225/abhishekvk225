using System.Security.Cryptography;
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

/// <summary>The ticket handed out after the password step; its token goes to the client, only its hash is stored.</summary>
public sealed record MfaTicket(string Token, int ExpiresInSeconds);

public interface IMfaService
{
    Task<Result<MfaStatusDto>> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Creates (or restarts) a pending enrolment for the signed-in user and returns the secret ONCE.</summary>
    Task<Result<MfaEnrolmentDto>> BeginEnrolmentAsync(CancellationToken cancellationToken);

    Task<Result<MfaEnabledDto>> ConfirmEnrolmentAsync(ConfirmMfaRequest request, CancellationToken cancellationToken);

    Task<Result<RecoveryCodesDto>> RegenerateRecoveryCodesAsync(RegenerateRecoveryCodesRequest request, CancellationToken cancellationToken);

    /// <summary>Second sign-in step: trades a live challenge plus a valid code for the normal token pair.</summary>
    Task<Result<LoginResponse>> VerifyChallengeAsync(VerifyMfaRequest request, CancellationToken cancellationToken);

    /// <summary>Stages a new challenge for a user whose password was just proven (the caller saves the unit of work).</summary>
    Task<MfaTicket> StartChallengeAsync(User user, DateTime now, CancellationToken cancellationToken);

    /// <summary>Another Super Admin switches off a user's MFA. <paramref name="clientId"/> is the route's client for client users, null for staff.</summary>
    Task<Result> ResetAsync(Guid? clientId, Guid userId, ResetMfaRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// TOTP second factor for sign-in. Secrets are generated here, encrypted with the platform key (purpose "mfa-secret") and shown
/// once. A challenge lives minutes, is single-use, and dies after a handful of attempts; every code attempt also counts against the
/// account's sign-in throttle, and an accepted TOTP step can never be used twice.
/// </summary>
public sealed class MfaService : IMfaService
{
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
    private const int RecoveryCodeLength = 10;

    private static readonly Error InvalidChallenge = Error.Unauthenticated(ErrorCodes.MfaChallengeInvalid, "The sign-in has expired. Please sign in again.");
    private static readonly Error InvalidCode = Error.Unauthenticated(ErrorCodes.MfaCodeInvalid, "The verification code is not valid.");

    private readonly IUserRepository _users;
    private readonly IMfaRepository _mfa;
    private readonly IMfaAtomics _atomics;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ILoginHistoryRepository _history;
    private readonly ILoginThrottle _throttle;
    private readonly ISessionIssuer _sessionIssuer;
    private readonly IMfaPolicy _policy;
    private readonly IPlatformCrypto _crypto;
    private readonly ISecureTokenService _tokens;
    private readonly IClientAccessGuard _clientGuard;
    private readonly ISessionValidator _sessions;
    private readonly IAuditService _audit;
    private readonly ICurrentUser _currentUser;
    private readonly IRequestInfo _request;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantScope _scope;
    private readonly TimeProvider _time;
    private readonly MfaOptions _options;
    private readonly AuthOptions _auth;
    private readonly ILogger<MfaService> _logger;

    public MfaService(
        IUserRepository users,
        IMfaRepository mfa,
        IMfaAtomics atomics,
        IRefreshTokenRepository refreshTokens,
        ILoginHistoryRepository history,
        ILoginThrottle throttle,
        ISessionIssuer sessionIssuer,
        IMfaPolicy policy,
        IPlatformCrypto crypto,
        ISecureTokenService tokens,
        IClientAccessGuard clientGuard,
        ISessionValidator sessions,
        IAuditService audit,
        ICurrentUser currentUser,
        IRequestInfo request,
        IUnitOfWork unitOfWork,
        ITenantScope scope,
        TimeProvider time,
        IOptions<MfaOptions> options,
        IOptions<AuthOptions> auth,
        ILogger<MfaService> logger)
    {
        _users = users;
        _mfa = mfa;
        _atomics = atomics;
        _refreshTokens = refreshTokens;
        _history = history;
        _throttle = throttle;
        _sessionIssuer = sessionIssuer;
        _policy = policy;
        _crypto = crypto;
        _tokens = tokens;
        _clientGuard = clientGuard;
        _sessions = sessions;
        _audit = audit;
        _currentUser = currentUser;
        _request = request;
        _unitOfWork = unitOfWork;
        _scope = scope;
        _time = time;
        _options = options.Value;
        _auth = auth.Value;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ enrolment (signed-in user)

    public async Task<Result<MfaStatusDto>> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync(cancellationToken) is not { } user)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, "The session is no longer valid. Please sign in again.");
        }

        var roles = await _users.GetRoleNamesAsync(user.Id, cancellationToken);
        var record = await _mfa.GetAsync(user.Id, cancellationToken);
        var remaining = user.TwoFactorEnabled ? await _mfa.CountUnusedCodesAsync(user.Id, cancellationToken) : 0;
        return new MfaStatusDto(
            _policy.IsAvailable(user), user.TwoFactorEnabled, await _policy.EnrolmentRequiredAsync(user, roles, cancellationToken),
            record is { IsConfirmed: false }, remaining);
    }

    public async Task<Result<MfaEnrolmentDto>> BeginEnrolmentAsync(CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync(cancellationToken) is not { } user)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, "The session is no longer valid. Please sign in again.");
        }

        if (!_policy.IsAvailable(user))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Two-factor authentication is not available for this account.");
        }

        if (user.TwoFactorEnabled)
        {
            return Error.Conflict("MFA_ALREADY_ENABLED", "Two-factor authentication is already enabled. Ask another administrator to reset it first.");
        }

        var secret = RandomNumberGenerator.GetBytes(Totp.SecretBytes);
        try
        {
            var now = Now;
            var encrypted = _crypto.Protect(secret, CryptoPurposes.MfaSecret, ContextFor(user.Id));
            var existing = await _mfa.GetAsync(user.Id, cancellationToken);
            if (existing is null)
            {
                _mfa.Add(UserMfa.StartEnrolment(user, encrypted, now));
            }
            else
            {
                existing.Restart(encrypted, now);
            }

            _audit.Record(new AuditEntry(AuditActions.MfaEnrolmentStarted, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var base32 = Base32.Encode(secret);
            return new MfaEnrolmentDto(base32, OtpauthUri(user.Email, base32), _options.Issuer, user.Email, "SHA1", Totp.Digits, Totp.StepSeconds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<Result<MfaEnabledDto>> ConfirmEnrolmentAsync(ConfirmMfaRequest request, CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync(cancellationToken) is not { } user)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, "The session is no longer valid. Please sign in again.");
        }

        var record = await _mfa.GetAsync(user.Id, cancellationToken);
        if (user.TwoFactorEnabled || record is null || record.IsConfirmed)
        {
            return Error.Conflict("MFA_NOT_PENDING", "There is no pending two-factor setup. Start the setup again.");
        }

        var now = Now;
        var step = MatchStep(record, request.Code, now);
        if (step is null)
        {
            if (record.RegisterConfirmFailure())
            {
                _mfa.Remove(record); // too many wrong codes: the secret may have been seen, start over with a new one
                _audit.Record(new AuditEntry(AuditActions.MfaEnrolmentAborted, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return Error.Validation("The verification code is not valid.", new Dictionary<string, string[]> { ["code"] = ["The verification code is not valid."] });
        }

        record.Confirm(step.Value, now);
        user.EnableTwoFactor(); // ends earlier sessions: from here on every live token belongs to a second-factor sign-in
        var codes = IssueRecoveryCodes(user, now);
        foreach (var token in await _refreshTokens.GetActiveForUserAsync(user.Id, cancellationToken))
        {
            token.Revoke(now, "mfa-enabled");
        }

        var session = await _sessionIssuer.IssueAsync(user, familyId: null, now, cancellationToken);
        _audit.Record(new AuditEntry(AuditActions.MfaEnabled, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _sessions.Invalidate(user.Id);
        return new MfaEnabledDto(codes, session);
    }

    public async Task<Result<RecoveryCodesDto>> RegenerateRecoveryCodesAsync(RegenerateRecoveryCodesRequest request, CancellationToken cancellationToken)
    {
        if (await CurrentUserAsync(cancellationToken) is not { } user)
        {
            return Error.Unauthenticated(ErrorCodes.Unauthenticated, "The session is no longer valid. Please sign in again.");
        }

        var record = await _mfa.GetAsync(user.Id, cancellationToken);
        if (!user.TwoFactorEnabled || record is not { IsConfirmed: true })
        {
            return Error.Conflict("MFA_NOT_ENABLED", "Two-factor authentication is not enabled.");
        }

        var now = Now;

        // A stolen session must not be able to mint recovery codes: it needs a live authenticator code, and the same account
        // throttle as sign-in applies to guessing it.
        var attempt = user.IsLockedOut(now)
            ? new AttemptState(true, 0, user.LockoutEnd)
            : await _throttle.ReserveAttemptAsync(user.Id, now, _auth.MaxFailedAttempts, TimeSpan.FromMinutes(_auth.LockoutMinutes), cancellationToken);
        if (attempt.Locked)
        {
            return Error.Validation("Too many failed attempts. The account is temporarily locked.");
        }

        if (MatchStep(record, request.Code, now) is not { } step || !await _atomics.TryAdvanceStepAsync(user.Id, step, cancellationToken))
        {
            return Error.Validation("The verification code is not valid.", new Dictionary<string, string[]> { ["code"] = ["The verification code is not valid."] });
        }

        await _throttle.ClearAsync(user.Id, cancellationToken);
        foreach (var old in await _mfa.GetCodesAsync(user.Id, cancellationToken))
        {
            _mfa.Remove(old);
        }

        var codes = IssueRecoveryCodes(user, now);
        _audit.Record(new AuditEntry(AuditActions.MfaRecoveryCodesRegenerated, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new RecoveryCodesDto(codes);
    }

    // ------------------------------------------------------------------ sign-in second step

    public async Task<MfaTicket> StartChallengeAsync(User user, DateTime now, CancellationToken cancellationToken)
    {
        await _atomics.PurgeChallengesAsync(user.Id, now, cancellationToken);
        var raw = _tokens.CreateToken();
        var lifetime = TimeSpan.FromMinutes(_options.ChallengeMinutes);
        _mfa.Add(MfaChallenge.Issue(user, _tokens.Hash(raw), now, lifetime));
        return new MfaTicket(raw, (int)lifetime.TotalSeconds);
    }

    public async Task<Result<LoginResponse>> VerifyChallengeAsync(VerifyMfaRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scope.BeginPlatform("mfa challenge verification");
        var now = Now;
        var max = _options.MaxChallengeAttempts;

        var challenge = await _mfa.FindChallengeAsync(_tokens.Hash(request.ChallengeToken.Trim()), cancellationToken);
        if (challenge is null || !challenge.IsLive(now, max))
        {
            return InvalidChallenge;
        }

        // The attempt is counted BEFORE the code is looked at, in one statement: parallel guesses each consume one of the few allowed.
        if (!await _atomics.TryRegisterAttemptAsync(challenge.Id, max, now, cancellationToken))
        {
            return InvalidChallenge;
        }

        var user = await _users.GetByIdAsync(challenge.UserId, cancellationToken);
        if (user is null || !user.CanSignIn() || !user.TwoFactorEnabled)
        {
            return InvalidChallenge;
        }

        if (!user.IsPlatformUser && await _clientGuard.CheckAsync(user.ClientId, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var attempt = user.IsLockedOut(now)
            ? new AttemptState(true, 0, user.LockoutEnd)
            : await _throttle.ReserveAttemptAsync(user.Id, now, _auth.MaxFailedAttempts, TimeSpan.FromMinutes(_auth.LockoutMinutes), cancellationToken);
        if (attempt.Locked)
        {
            RecordLogin(user, LoginOutcome.LockedOut, "locked during second factor");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return InvalidChallenge;
        }

        var method = await CheckCodeAsync(user, request.Code, now, cancellationToken);
        if (method is null)
        {
            RecordLogin(user, LoginOutcome.MfaFailed, "bad second factor");
            if (challenge.Attempts + 1 >= max)
            {
                // The ticket is spent: the next attempt needs a new password sign-in (and the account throttle has counted these too).
                _audit.Record(new AuditEntry(AuditActions.MfaChallengeExhausted, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
                _logger.LogWarning("MFA challenge for user {UserId} exhausted its attempts", user.Id);
            }

            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return InvalidCode;
        }

        if (!await _atomics.TryConsumeChallengeAsync(challenge.Id, now, cancellationToken))
        {
            return InvalidChallenge; // lost a race with another submission of the same ticket
        }

        await _throttle.ClearAsync(user.Id, cancellationToken);
        user.RegisterSuccessfulLogin(now);
        var session = await _sessionIssuer.IssueAsync(user, familyId: null, now, cancellationToken);
        RecordLogin(user, LoginOutcome.Success, null);
        _audit.Record(new AuditEntry(AuditActions.MfaVerified, nameof(User), user.Id.ToString(), user.ClientId, NewValues: new { Method = method }, ActorId: user.Id));
        await _unitOfWork.SaveChangesAsync(CancellationToken.None);
        return session;
    }

    // ------------------------------------------------------------------ reset by another administrator

    public async Task<Result> ResetAsync(Guid? clientId, Guid userId, ResetMfaRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.ActorId is not { } actorId || _currentUser.ActorType != ActorType.User || !_currentUser.IsPlatformUser
            || !_currentUser.Roles.Contains(SystemRoles.SuperAdmin, StringComparer.Ordinal))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only a Super Admin can reset two-factor authentication.");
        }

        if (actorId == userId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Another Super Admin must reset your two-factor authentication.");
        }

        var user = await _users.GetByIdAsync(userId, cancellationToken);
        if (user is null || (clientId is { } c ? user.ClientId != c || user.IsPlatformUser : !user.IsPlatformUser))
        {
            return Error.NotFound();
        }

        var record = await _mfa.GetAsync(user.Id, cancellationToken);
        if (!user.TwoFactorEnabled && record is null)
        {
            return Error.Conflict("MFA_NOT_ENABLED", "Two-factor authentication is not enabled for this user.");
        }

        var now = Now;
        if (record is not null)
        {
            _mfa.Remove(record);
        }

        foreach (var code in await _mfa.GetCodesAsync(user.Id, cancellationToken))
        {
            _mfa.Remove(code);
        }

        user.DisableTwoFactor();
        foreach (var token in await _refreshTokens.GetActiveForUserAsync(user.Id, cancellationToken))
        {
            token.Revoke(now, "mfa-reset");
        }

        _audit.Record(new AuditEntry(AuditActions.MfaReset, nameof(User), user.Id.ToString(), user.ClientId,
            NewValues: new { request.Reason, TargetUserId = user.Id }, ActorId: actorId));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _sessions.Invalidate(user.Id);
        return Result.Success();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<User?> CurrentUserAsync(CancellationToken cancellationToken) =>
        _currentUser.ActorId is { } id && _currentUser.ActorType == ActorType.User ? await _users.GetByIdAsync(id, cancellationToken) : null;

    private static string ContextFor(Guid userId) => userId.ToString("N");

    /// <summary>The TOTP step the code matches (within the drift window), or null. Never throws on a damaged secret: that just fails closed.</summary>
    private long? MatchStep(UserMfa record, string code, DateTime now)
    {
        byte[] secret;
        try
        {
            secret = _crypto.Unprotect(record.SecretEnc, CryptoPurposes.MfaSecret, ContextFor(record.UserId));
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "The MFA secret of user {UserId} could not be decrypted", record.UserId);
            return null;
        }

        try
        {
            return Totp.TryMatch(secret, NormalizeCode(code), now, _options.Window);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>"totp" or "recovery" when the code was accepted (and consumed), null otherwise.</summary>
    private async Task<string?> CheckCodeAsync(User user, string code, DateTime now, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCode(code);
        if (normalized.Length == Totp.Digits && normalized.All(char.IsAsciiDigit))
        {
            var record = await _mfa.GetAsync(user.Id, cancellationToken);
            if (record is not { IsConfirmed: true } || MatchStep(record, normalized, now) is not { } step)
            {
                return null;
            }

            return await _atomics.TryAdvanceStepAsync(user.Id, step, cancellationToken) ? "totp" : null; // false = replayed code
        }

        var recovery = normalized.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (recovery.Length != RecoveryCodeLength)
        {
            return null;
        }

        var row = await _mfa.FindUnusedCodeAsync(user.Id, _tokens.Hash(recovery), cancellationToken);
        if (row is null || !await _atomics.TryUseRecoveryCodeAsync(row.Id, now, cancellationToken))
        {
            return null;
        }

        _audit.Record(new AuditEntry(AuditActions.MfaRecoveryCodeUsed, nameof(User), user.Id.ToString(), user.ClientId, ActorId: user.Id));
        return "recovery";
    }

    private static string NormalizeCode(string code) => (code ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

    private IReadOnlyList<string> IssueRecoveryCodes(User user, DateTime now)
    {
        var codes = new List<string>(_options.RecoveryCodeCount);
        for (var i = 0; i < _options.RecoveryCodeCount; i++)
        {
            var chars = Enumerable.Range(0, RecoveryCodeLength).Select(_ => RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)]).ToArray();
            var raw = new string(chars);
            _mfa.Add(MfaRecoveryCode.Issue(user, _tokens.Hash(raw), now));
            codes.Add($"{raw[..5]}-{raw[5..]}");
        }

        return codes;
    }

    private string OtpauthUri(string email, string secretBase32)
    {
        var issuer = Uri.EscapeDataString(_options.Issuer);
        return $"otpauth://totp/{issuer}:{Uri.EscapeDataString(email)}?secret={secretBase32}&issuer={issuer}&algorithm=SHA1&digits={Totp.Digits}&period={Totp.StepSeconds}";
    }

    private void RecordLogin(User user, LoginOutcome outcome, string? reason) =>
        _history.Add(new LoginHistory
        {
            ClientId = user.ClientId,
            UserId = user.Id,
            EmailAttempted = user.Email.Length > User.EmailMaxLength ? user.Email[..User.EmailMaxLength] : user.Email,
            Outcome = outcome,
            FailureReason = reason,
            IpAddress = _request.IpAddress,
            UserAgent = _request.UserAgent is { Length: > 300 } ua ? ua[..300] : _request.UserAgent,
            CorrelationId = _request.CorrelationId,
            OccurredAt = Now,
        });
}
