using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

public enum ResetEmailKind
{
    Reset,
    Invitation,
}

/// <summary>
/// Issues single-use set-password tokens and emails the link. Used by "forgot password", by administrators resetting a user
/// and by invitations (a new user is created with an unusable password and chooses their own through this link) — so no
/// administrator ever sees or transmits a password.
/// </summary>
public interface IPasswordResetService
{
    /// <summary>Stages a new token (invalidating older unused ones) in the unit of work and returns the raw value to email after saving.</summary>
    Task<string> IssueAsync(User user, ResetEmailKind kind, CancellationToken cancellationToken);

    Task SendAsync(User user, string rawToken, ResetEmailKind kind, CancellationToken cancellationToken);
}

public sealed class PasswordResetService : IPasswordResetService
{
    private readonly IPasswordResetTokenRepository _tokens;
    private readonly ISecureTokenService _secure;
    private readonly IEmailOutbox _outbox;
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;

    public PasswordResetService(
        IPasswordResetTokenRepository tokens,
        ISecureTokenService secure,
        IEmailOutbox outbox,
        IOptions<AuthOptions> options,
        TimeProvider time)
    {
        _tokens = tokens;
        _secure = secure;
        _outbox = outbox;
        _options = options.Value;
        _time = time;
    }

    private static string Plain(string value) => value.ReplaceLineEndings(" ").Trim();

    public async Task<string> IssueAsync(User user, ResetEmailKind kind, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var old in await _tokens.GetUnusedForUserAsync(user.Id, now, cancellationToken))
        {
            old.MarkUsed(now);
        }

        var raw = _secure.CreateToken();
        var lifetime = kind == ResetEmailKind.Invitation ? TimeSpan.FromHours(_options.InvitationHours) : TimeSpan.FromMinutes(_options.PasswordResetMinutes);
        _tokens.Add(PasswordResetToken.Issue(user, _secure.Hash(raw), now, lifetime));
        return raw;
    }

    public Task SendAsync(User user, string rawToken, ResetEmailKind kind, CancellationToken cancellationToken)
    {
        var link = _options.PasswordResetUrlTemplate
            .Replace("{email}", Uri.EscapeDataString(user.Email), StringComparison.Ordinal)
            .Replace("{token}", Uri.EscapeDataString(rawToken), StringComparison.Ordinal);

        var message = kind == ResetEmailKind.Invitation
            ? new EmailMessage(
                user.Email,
                "You have been invited to NexaVerify",
                $"Hello {Plain(user.FullName)},\n\nAn account has been created for you. Use the link below to choose your password. It expires in {_options.InvitationHours} hours and can be used once.\n\n{link}")
            : new EmailMessage(
                user.Email,
                "Reset your NexaVerify password",
                $"Hello {Plain(user.FullName)},\n\nUse the link below to choose a new password. It expires in {_options.PasswordResetMinutes} minutes and can be used once.\n\n{link}\n\nIf you did not request this, you can ignore this email.");

        _outbox.Enqueue(message);
        return Task.CompletedTask;
    }
}
