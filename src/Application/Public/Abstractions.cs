using Microsoft.Extensions.Options;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Public;

/// <summary>Verifies the bot-protection token a visitor's browser obtained. Implementations fail closed.</summary>
public interface ICaptchaVerifier
{
    /// <summary>True when no provider is configured, or the provider confirmed the token. False on a missing/invalid token and on any provider failure.</summary>
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken);
}

public enum PublicThrottlePolicy
{
    SignupPerIp,
    SignupPerEmail,
    ContactPerIp,
}

/// <summary>Shared (all API nodes) limits for anonymous callers. The subject is an IP address or a normalised email; only a hash of it is ever stored.</summary>
public interface IPublicThrottle
{
    /// <summary>Takes one permit; false when the limit of <paramref name="policy"/> is used up for <paramref name="subject"/>.</summary>
    Task<bool> TryAcquireAsync(PublicThrottlePolicy policy, string subject, CancellationToken cancellationToken);
}

public interface IPendingSignupRepository
{
    /// <summary>The unconsumed, unexpired sign-up of this address (not tracked).</summary>
    Task<PendingSignup?> FindLiveAsync(string normalizedEmail, DateTime now, CancellationToken cancellationToken);

    /// <summary>Every unconsumed row of this address, expired or not (they all hold the one-per-address slot).</summary>
    Task<IReadOnlyList<PendingSignup>> GetUnconsumedAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>Like <see cref="FindLiveAsync"/> but tracked, so the caller can re-issue its token.</summary>
    Task<PendingSignup?> FindLiveForUpdateAsync(string normalizedEmail, DateTime now, CancellationToken cancellationToken);

    void Add(PendingSignup signup);

    void Remove(PendingSignup signup);
}

/// <summary>The single-statement transition that makes a verification link usable exactly once.</summary>
public interface IPendingSignupAtomics
{
    /// <summary>Marks the row consumed (and erases its password hash) only if it is unconsumed, unexpired and the token hash matches. True for exactly one concurrent caller.</summary>
    Task<bool> TryClaimAsync(Guid id, byte[] tokenHash, DateTime now, CancellationToken cancellationToken);
}

public interface IContactRequestRepository
{
    void Add(ContactRequest request);

    Task<(IReadOnlyList<ContactRequest> Items, int Total)> ListAsync(int skip, int take, CancellationToken cancellationToken);
}

/// <summary>Refuses addresses at throw-away mail providers: a small built-in list plus <c>Signup:DisposableEmailDomains</c>.</summary>
public sealed class DisposableEmailPolicy
{
    private static readonly string[] BuiltIn =
    [
        "mailinator.com", "guerrillamail.com", "guerrillamail.net", "guerrillamail.org", "sharklasers.com", "grr.la", "10minutemail.com",
        "10minutemail.net", "tempmail.com", "temp-mail.org", "temp-mail.io", "tempmailo.com", "throwawaymail.com", "yopmail.com", "yopmail.net",
        "trashmail.com", "trashmail.net", "getnada.com", "nada.email", "maildrop.cc", "dispostable.com", "fakeinbox.com", "mailnesia.com",
        "mintemail.com", "mytemp.email", "spam4.me", "spamgourmet.com", "burnermail.io", "mohmal.com", "emailondeck.com", "moakt.com",
        "tmail.ws", "tmpmail.org", "tmpmail.net", "discard.email", "mailcatch.com", "inboxkitten.com", "harakirimail.com", "33mail.com",
    ];

    private readonly HashSet<string> _domains;

    public DisposableEmailPolicy(IOptions<SignupOptions> options)
    {
        _domains = new HashSet<string>(BuiltIn, StringComparer.OrdinalIgnoreCase);
        foreach (var domain in options.Value.DisposableEmailDomains.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            _domains.Add(domain.Trim().TrimStart('@').TrimStart('.'));
        }
    }

    public bool IsDisposable(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (email is null || at < 0)
        {
            return false;
        }

        var domain = email[(at + 1)..].Trim().TrimEnd('.');
        for (var candidate = domain; candidate.Length > 0;)
        {
            if (_domains.Contains(candidate))
            {
                return true;
            }

            var dot = candidate.IndexOf('.', StringComparison.Ordinal);
            candidate = dot < 0 ? string.Empty : candidate[(dot + 1)..];
        }

        return false;
    }
}
