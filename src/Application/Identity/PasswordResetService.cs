using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Messaging;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Messaging;

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
    /// <summary>Stages a new token (invalidating older unused ones) in the unit of work and returns the raw value to put in the email.</summary>
    Task<string> IssueAsync(User user, ResetEmailKind kind, CancellationToken cancellationToken);

    /// <summary>Stages the email carrying the link in the current unit of work: call it BEFORE the save that stores the token, so both commit together.</summary>
    Task SendAsync(User user, string rawToken, ResetEmailKind kind, CancellationToken cancellationToken);
}

public sealed class PasswordResetService : IPasswordResetService
{
    private readonly IPasswordResetTokenRepository _tokens;
    private readonly ISecureTokenService _secure;
    private readonly IEmailSender _email;
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;

    public PasswordResetService(
        IPasswordResetTokenRepository tokens,
        ISecureTokenService secure,
        IEmailSender email,
        IOptions<AuthOptions> options,
        TimeProvider time)
    {
        _tokens = tokens;
        _secure = secure;
        _email = email;
        _options = options.Value;
        _time = time;
    }

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

    public async Task SendAsync(User user, string rawToken, ResetEmailKind kind, CancellationToken cancellationToken)
    {
        // The configured template is an absolute URL; only its path and query shape are used. The host always comes from the public base URL.
        var (path, query) = LinkShape(_options.PasswordResetUrlTemplate, user.Email, rawToken);
        var invitation = kind == ResetEmailKind.Invitation;
        var request = new EmailRequest(invitation ? EmailTemplate.UserInvite : EmailTemplate.PasswordReset, user.Email, user.FullName)
            .Text("name", user.FullName)
            .Number("hours", _options.InvitationHours)
            .Number("minutes", _options.PasswordResetMinutes);
        request.Link("action", path, query);
        await _email.SendAsync(request, cancellationToken);
    }

    /// <summary>Splits <c>https://host/reset-password?email={email}&amp;token={token}</c> into its path and query with the placeholders filled in (wherever they appear).</summary>
    public static (string Path, (string Key, string Value)[] Query) LinkShape(string template, string email, string rawToken)
    {
        const string emailMarker = "NVEMAILMARKER";
        const string tokenMarker = "NVTOKENMARKER";
        string Fill(string text) => text.Replace(emailMarker, email, StringComparison.Ordinal).Replace(tokenMarker, rawToken, StringComparison.Ordinal);

        var uri = new Uri(template.Replace("{email}", emailMarker, StringComparison.Ordinal).Replace("{token}", tokenMarker, StringComparison.Ordinal), UriKind.Absolute);
        var path = string.Join('/', uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => Fill(Uri.UnescapeDataString(segment))));
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Select(parts => (Key: Uri.UnescapeDataString(parts[0]), Value: Fill(parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty)))
            .ToArray();
        return (path, query);
    }
}
