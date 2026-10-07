using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

internal static class IdentityMapping
{
    public static UserSummary ToSummary(this User user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email, user.FullName, user.IsPlatformUser, user.IsPlatformUser ? null : user.ClientId, roles);
}
