using System.Globalization;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Application.Messaging;

/// <summary>The security notices about an account, built in one place so every flow words and links them the same way.</summary>
public static class SecurityNotices
{
    public static string Stamp(DateTime utc) => utc.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static EmailRequest PasswordChanged(User user, DateTime at, string how) =>
        new EmailRequest(EmailTemplate.PasswordChanged, user.Email, user.FullName)
            .Text("name", user.FullName)
            .Text("when", Stamp(at))
            .Text("how", how)
            .Link("reset", "forgot-password");

    public static EmailRequest MfaEnabled(User user, DateTime at) =>
        new EmailRequest(EmailTemplate.MfaEnabled, user.Email, user.FullName)
            .Text("name", user.FullName)
            .Text("when", Stamp(at))
            .Link("reset", "forgot-password");

    public static EmailRequest MfaDisabled(User user, DateTime at) =>
        new EmailRequest(EmailTemplate.MfaDisabled, user.Email, user.FullName)
            .Text("name", user.FullName)
            .Text("when", Stamp(at))
            .Link("login", "login");

    public static EmailRequest MfaRecoveryCodeUsed(User user, DateTime at) =>
        new EmailRequest(EmailTemplate.MfaRecoveryCodeUsed, user.Email, user.FullName)
            .Text("name", user.FullName)
            .Text("when", Stamp(at))
            .Link("reset", "forgot-password");
}
