using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Public;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Public;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Identity;

public interface ISignupService
{
    /// <summary>Starts a self-service sign-up. Succeeds identically for new, pending and already-registered addresses.</summary>
    Task<Result> SignupAsync(SignupRequest request, CancellationToken cancellationToken);

    /// <summary>Re-sends the verification email of a live pending sign-up with a fresh token. Succeeds identically for every address.</summary>
    Task<Result> ResendAsync(ResendSignupRequest request, CancellationToken cancellationToken);

    /// <summary>Completes a sign-up from the emailed link: once only, all-or-nothing. Every failure is the same generic validation error.</summary>
    Task<Result> VerifyAsync(VerifySignupRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Public self-service onboarding. Nothing here reveals whether an address already has an account: the response, its timing and its
/// side effects visible to the caller are the same for every outcome (the difference is only the email that is sent). Anonymous work
/// runs in a narrowly scoped, reasoned platform scope because the tenant does not exist until verification succeeds.
/// </summary>
public sealed partial class SignupService : ISignupService
{
    public const string Source = "public-signup";
    public const string VerificationSubject = "Confirm your NexaVerify account";
    public const string ExistingAccountSubject = "You already have a NexaVerify account";
    public const string TrialLicenseName = "Free trial";

    private static readonly Error InvalidLink = Error.Validation("The verification link is invalid or has expired.");

    private readonly IPendingSignupRepository _pending;
    private readonly IPendingSignupAtomics _atomics;
    private readonly IUserRepository _users;
    private readonly IClientRepository _clients;
    private readonly IPlanRepository _plans;
    private readonly IPasswordHasher _hasher;
    private readonly ISecureTokenService _secure;
    private readonly IEmailOutbox _outbox;
    private readonly IPublicThrottle _throttle;
    private readonly ICaptchaVerifier _captcha;
    private readonly IRequestInfo _request;
    private readonly ITenantScope _scope;
    private readonly IClientProvisioner _provisioner;
    private readonly ILicenseService _licenses;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SignupOptions _options;
    private readonly PortalLinksOptions _portal;
    private readonly TimeProvider _time;
    private readonly ILogger<SignupService> _logger;

    public SignupService(
        IPendingSignupRepository pending,
        IPendingSignupAtomics atomics,
        IUserRepository users,
        IClientRepository clients,
        IPlanRepository plans,
        IPasswordHasher hasher,
        ISecureTokenService secure,
        IEmailOutbox outbox,
        IPublicThrottle throttle,
        ICaptchaVerifier captcha,
        IRequestInfo request,
        ITenantScope scope,
        IClientProvisioner provisioner,
        ILicenseService licenses,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        IOptions<SignupOptions> options,
        IOptions<PortalLinksOptions> portal,
        TimeProvider time,
        ILogger<SignupService> logger)
    {
        _pending = pending;
        _atomics = atomics;
        _users = users;
        _clients = clients;
        _plans = plans;
        _hasher = hasher;
        _secure = secure;
        _outbox = outbox;
        _throttle = throttle;
        _captcha = captcha;
        _request = request;
        _scope = scope;
        _provisioner = provisioner;
        _licenses = licenses;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _portal = portal.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Case and surrounding whitespace never make two spellings of one address different accounts.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public async Task<Result> SignupAsync(SignupRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Error.Forbidden(ErrorCodes.SignupDisabled, "Sign-up is not available right now.");
        }

        var timer = Stopwatch.StartNew();
        try
        {
            if (!string.IsNullOrEmpty(request.Website))
            {
                LogHoneypot();
                return Result.Success(); // accepted and dropped: a bot learns nothing
            }

            if (!await _throttle.TryAcquireAsync(PublicThrottlePolicy.SignupPerIp, _request.IpAddress ?? "unknown", cancellationToken))
            {
                LogRateLimited("ip");
                return Error.TooManyRequests(ErrorCodes.RateLimited, "Too many sign-up attempts. Try again later.");
            }

            if (!await _captcha.VerifyAsync(request.CaptchaToken, _request.IpAddress, cancellationToken))
            {
                LogCaptchaFailed();
                return new Error(ErrorCodes.CaptchaFailed, "The security check failed. Reload the page and try again.", ErrorType.Validation,
                    new Dictionary<string, string[]> { ["captchaToken"] = ["The security check failed."] });
            }

            var email = NormalizeEmail(request.Email);

            // Always pay for the password hash, so a known address costs no less than a new one.
            var passwordHash = _hasher.Hash(request.Password);

            if (!await _throttle.TryAcquireAsync(PublicThrottlePolicy.SignupPerEmail, email, cancellationToken))
            {
                LogRateLimited("email");
                return Result.Success(); // same answer as any other outcome; no further email is sent
            }

            using var scope = _scope.BeginPlatform("public signup");
            if (await _users.EmailExistsAsync(User.Normalize(email), cancellationToken))
            {
                _outbox.Enqueue(ExistingAccountMessage(email, request.FullName));
                LogSignupRequested("existing");
                return Result.Success();
            }

            var token = _secure.CreateToken();
            if (await StagePendingAsync(request, email, passwordHash, _secure.Hash(token), cancellationToken))
            {
                _outbox.Enqueue(VerificationMessage(email, request.FullName, request.CompanyName, token));
                LogSignupRequested("new");
            }

            return Result.Success();
        }
        finally
        {
            await PadAsync(timer, cancellationToken);
        }
    }

    public async Task<Result> ResendAsync(ResendSignupRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Error.Forbidden(ErrorCodes.SignupDisabled, "Sign-up is not available right now.");
        }

        var timer = Stopwatch.StartNew();
        try
        {
            if (!await _throttle.TryAcquireAsync(PublicThrottlePolicy.SignupPerIp, _request.IpAddress ?? "unknown", cancellationToken))
            {
                LogRateLimited("ip");
                return Error.TooManyRequests(ErrorCodes.RateLimited, "Too many attempts. Try again later.");
            }

            var email = NormalizeEmail(request.Email);
            if (!await _throttle.TryAcquireAsync(PublicThrottlePolicy.SignupPerEmail, email, cancellationToken))
            {
                LogRateLimited("email");
                return Result.Success();
            }

            using var scope = _scope.BeginPlatform("public signup resend");
            var pending = await _pending.FindLiveForUpdateAsync(User.Normalize(email), Now, cancellationToken);
            var token = _secure.CreateToken();
            if (pending is not null && pending.Reissue(_secure.Hash(token), Now, TimeSpan.FromHours(_options.VerificationHours)))
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                _outbox.Enqueue(VerificationMessage(pending.Email.ToLowerInvariant(), pending.FullName, pending.CompanyName, token));
                LogSignupRequested("resent");
            }

            return Result.Success();
        }
        finally
        {
            await PadAsync(timer, cancellationToken);
        }
    }

    public async Task<Result> VerifyAsync(VerifySignupRequest request, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var scope = _scope.BeginPlatform("public signup verification");
            var now = Now;
            var email = NormalizeEmail(request.Email);
            var hash = _secure.Hash(request.Token);

            var pending = await _pending.FindLiveAsync(User.Normalize(email), now, cancellationToken);
            if (pending is null || !CryptographicOperations.FixedTimeEquals(pending.TokenHash, hash))
            {
                LogVerificationFailed("unknown-or-expired");
                return InvalidLink;
            }

            return await CompleteAsync(pending, hash, now, cancellationToken) ? Result.Success() : InvalidLink;
        }
        finally
        {
            await PadAsync(timer, cancellationToken);
        }
    }

    /// <summary>Replaces any earlier pending sign-up of the address. False when a concurrent sign-up for the same address won the slot.</summary>
    private async Task<bool> StagePendingAsync(SignupRequest request, string email, string passwordHash, byte[] tokenHash, CancellationToken cancellationToken)
    {
        try
        {
            await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    foreach (var old in await _pending.GetUnconsumedAsync(User.Normalize(email), ct))
                    {
                        _pending.Remove(old);
                    }

                    await _unitOfWork.SaveChangesAsync(ct); // free the one-per-address slot before the new row takes it

                    var pending = PendingSignup.Issue(request.CompanyName, request.FullName, email, passwordHash, tokenHash, Now,
                        TimeSpan.FromHours(_options.VerificationHours));
                    _pending.Add(pending);
                    _audit.Record(new AuditEntry("signup.requested", nameof(PendingSignup), pending.Id.ToString(), NewValues: new { Source, pending.CompanyName, pending.ExpiresAt }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);
            return true;
        }
        catch (UniqueConstraintViolationException)
        {
            _unitOfWork.ClearTracked();
            return false;
        }
    }

    private async Task<bool> CompleteAsync(PendingSignup pending, byte[] tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    if (await _users.EmailExistsAsync(pending.NormalizedEmail, ct))
                    {
                        LogVerificationFailed("email-registered");
                        return false;
                    }

                    // The single conditional UPDATE decides which concurrent caller creates the account; everything below
                    // commits or rolls back together with it, so a failure never burns the link.
                    if (!await _atomics.TryClaimAsync(pending.Id, tokenHash, now, ct))
                    {
                        LogVerificationFailed("already-used");
                        return false;
                    }

                    var details = new CreateClientRequest(
                        await NewCodeAsync(pending.CompanyName, ct), pending.CompanyName, null, pending.Email, null, null, null, null, null, null, null,
                        null, null, _options.DefaultTimeZone, "Self-service sign-up", pending.Email, pending.FullName);
                    var client = await _provisioner.ProvisionAsync(new ClientProvisioning(details, pending.PasswordHash, Source), ct);
                    if (client.IsFailure)
                    {
                        throw new SignupAbortedException(client.Error!);
                    }

                    var plan = await _plans.GetTrialAsync(ct);
                    var license = await _licenses.CreateAsync(
                        client.Value.Id,
                        new CreateLicenseRequest(plan?.Id, TrialLicenseName, _options.TrialCredits, null, now.AddDays(_options.TrialDays), "Created by self-service sign-up"),
                        ct);
                    if (license.IsFailure)
                    {
                        throw new SignupAbortedException(license.Error!);
                    }

                    _audit.Record(new AuditEntry("signup.verified", nameof(Client), client.Value.Id.ToString(), client.Value.Id,
                        NewValues: new { Source, _options.TrialCredits, _options.TrialDays, LicenseId = license.Value.Id }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    LogSignupCompleted(client.Value.Id);
                    return true;
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is SignupAbortedException or DomainException or UniqueConstraintViolationException)
        {
            _unitOfWork.ClearTracked();
            LogVerificationFailed(ex is SignupAbortedException aborted ? aborted.Error.Code : ex.GetType().Name);
            return false;
        }
    }

    private async Task<string> NewCodeAsync(string companyName, CancellationToken cancellationToken)
    {
        var baseCode = ClientCodes.BaseFrom(companyName);
        if (!await _clients.CodeExistsAsync(baseCode, cancellationToken))
        {
            return baseCode;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var code = ClientCodes.WithRandomSuffix(baseCode);
            if (!await _clients.CodeExistsAsync(code, cancellationToken))
            {
                return code;
            }
        }

        throw new SignupAbortedException(Error.Conflict(ErrorCodes.Conflict, "No free client code."));
    }

    private EmailMessage VerificationMessage(string email, string fullName, string companyName, string token)
    {
        var link = _portal.LinkTo("verify-email", ("email", email), ("token", token));
        return new EmailMessage(
            email,
            VerificationSubject,
            $"Hello {Plain(fullName)},\n\nThank you for signing up {Plain(companyName)} for NexaVerify. Confirm your email address to create your account and start your free trial ({_options.TrialCredits} credits for {_options.TrialDays} days). The link expires in {_options.VerificationHours} hours and can be used once.\n\n{link}\n\nIf you did not sign up, you can ignore this email.");
    }

    private EmailMessage ExistingAccountMessage(string email, string fullName) => new(
        email,
        ExistingAccountSubject,
        $"Hello {Plain(fullName)},\n\nSomeone used this email address to sign up for NexaVerify, but it already belongs to an account. You can sign in here:\n\n{_portal.LinkTo("login")}\n\nIf you forgot your password you can reset it here:\n\n{_portal.LinkTo("forgot-password")}\n\nIf this was not you, you can ignore this email.");

    private static string Plain(string value) => value.ReplaceLineEndings(" ").Trim();

    /// <summary>Sign-up responses take at least a fixed time, so latency does not separate known from unknown addresses.</summary>
    private async Task PadAsync(Stopwatch timer, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMilliseconds(_options.MinimumResponseMilliseconds) - timer.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Public sign-up requested ({Outcome})")]
    private partial void LogSignupRequested(string outcome);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning, Message = "Public sign-up dropped: honeypot field was filled")]
    private partial void LogHoneypot();

    [LoggerMessage(EventId = 8003, Level = LogLevel.Warning, Message = "Public sign-up refused: rate limit reached ({Scope})")]
    private partial void LogRateLimited(string scope);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Warning, Message = "Public sign-up refused: captcha verification failed")]
    private partial void LogCaptchaFailed();

    [LoggerMessage(EventId = 8005, Level = LogLevel.Information, Message = "Public sign-up completed; client {ClientId} created")]
    private partial void LogSignupCompleted(Guid clientId);

    [LoggerMessage(EventId = 8006, Level = LogLevel.Warning, Message = "Public sign-up verification failed ({Reason})")]
    private partial void LogVerificationFailed(string reason);

    private sealed class SignupAbortedException : Exception
    {
        public SignupAbortedException(Error error)
            : base(error.Code)
        {
            Error = error;
        }

        public Error Error { get; }
    }
}
