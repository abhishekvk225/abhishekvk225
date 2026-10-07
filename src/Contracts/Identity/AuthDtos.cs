using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Identity;

public sealed record LoginRequest(string Email, [property: Sensitive] string Password);

public sealed record RefreshRequest([property: Sensitive] string RefreshToken);

public sealed record LogoutRequest([property: Sensitive] string? RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, [property: Sensitive] string Token, [property: Sensitive] string NewPassword);

public sealed record ChangePasswordRequest(
    [property: Sensitive] string CurrentPassword,
    [property: Sensitive] string NewPassword);

public sealed record UserSummary(Guid Id, string Email, string FullName, bool IsPlatformUser, Guid? ClientId, IReadOnlyList<string> Roles);

public sealed record LoginResponse(
    [property: Sensitive] string AccessToken,
    string TokenType,
    int ExpiresIn,
    [property: Sensitive] string RefreshToken,
    bool MustChangePassword,
    UserSummary User);

public sealed record MeResponse(UserSummary User, IReadOnlyList<string> Permissions, bool MustChangePassword);
