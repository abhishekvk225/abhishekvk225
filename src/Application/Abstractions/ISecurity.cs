using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Abstractions;

public enum PasswordVerification
{
    Failed = 0,
    Success,
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Verify(string hash, string password);

    /// <summary>Spends the same time as a real verification (used for unknown accounts to prevent timing-based enumeration).</summary>
    void BurnTime(string password);
}

/// <summary>Generates and hashes opaque high-entropy tokens (refresh tokens, reset tokens, API keys).</summary>
public interface ISecureTokenService
{
    /// <summary>A new 256-bit URL-safe random token.</summary>
    string CreateToken();

    /// <summary>SHA-256 of the token — the only form that is ever stored.</summary>
    byte[] Hash(string token);
}

public sealed record AccessToken(string Value, int ExpiresInSeconds);

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user, IReadOnlyCollection<string> roles, bool mfaEnrolmentRequired = false);
}

public sealed record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
