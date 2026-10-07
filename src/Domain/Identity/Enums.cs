namespace NexaVerify.Domain.Identity;

public enum UserStatus
{
    Active = 0,
    Inactive,
    PendingActivation,
}

public enum RoleScope
{
    Platform = 0,
    Client,
}

public enum PermissionScope
{
    Platform = 0,
    Client,
    Both,
}

public enum LoginOutcome
{
    Success = 0,
    InvalidCredentials,
    LockedOut,
    Inactive,
    ClientSuspended,
    MfaFailed,
    PasswordReset,
}
