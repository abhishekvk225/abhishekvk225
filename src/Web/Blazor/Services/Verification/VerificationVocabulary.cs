using NexaVerify.Contracts.Verification;

namespace NexaVerify.Web.Services;

/// <summary>Wire values of <c>VerificationType</c> and what the portal needs to know about each (route, module key, who may submit).</summary>
public static class VerificationTypes
{
    public const string BankStatement = "BankStatement";
    public const string Pan = "Pan";
    public const string Passport = "Passport";

    public static IReadOnlyList<string> All { get; } = [BankStatement, Pan, Passport];

    public static string Label(string? type) => type switch
    {
        BankStatement => "Bank statement",
        Pan => "PAN",
        Passport => "Passport",
        _ => string.IsNullOrWhiteSpace(type) ? "Unknown" : type,
    };

    public static string ModuleKey(string? type) => type switch
    {
        BankStatement => VerificationModuleKeys.BankStatement,
        Pan => VerificationModuleKeys.Pan,
        Passport => VerificationModuleKeys.Passport,
        _ => string.Empty,
    };

    /// <summary>Portal page (relative, as used in the menu) where a new check of this type is started.</summary>
    public static string SubmitRoute(string? type) => type switch
    {
        BankStatement => "client/verification/bank-statement",
        Pan => "client/verification/pan",
        Passport => "client/verification/passport",
        _ => "client/verification",
    };

    /// <summary>The permission that lets a person submit, cancel and retry a check of this type.</summary>
    public static string? SubmitPermission(string? type) => type switch
    {
        BankStatement => WebPermissions.VerificationBankStatementSubmit,
        Pan => WebPermissions.VerificationPanSubmit,
        Passport => WebPermissions.VerificationPassportSubmit,
        _ => null,
    };
}

/// <summary>Overall request status (wire values of <c>VerificationStatus</c>).</summary>
public static class VerificationStatuses
{
    public const string Queued = "Queued";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string ReviewRequired = "ReviewRequired";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";

    public static IReadOnlyList<string> All { get; } = [Queued, Processing, Completed, ReviewRequired, Failed, Cancelled, Expired];

    /// <summary>Nothing more will happen on its own (a provider retry can still be started by the person).</summary>
    public static bool IsTerminal(string? status) => status is Completed or ReviewRequired or Failed or Cancelled or Expired;

    /// <summary>Still being worked on, so it can be cancelled.</summary>
    public static bool IsInProgress(string? status) => status is Queued or Processing;
}

/// <summary>State of the official (provider) check (wire values of <c>ExternalState</c>).</summary>
public static class ExternalStates
{
    public const string NotRequested = "NotRequested";
    public const string ProviderNotConfigured = "ProviderNotConfigured";
    public const string Pending = "Pending";
    public const string Verified = "Verified";
    public const string NotVerified = "NotVerified";
    public const string Inconclusive = "Inconclusive";
    public const string ProviderError = "ProviderError";
    public const string Timeout = "Timeout";

    public static IReadOnlyList<string> All { get; } = [NotRequested, ProviderNotConfigured, Pending, Verified, NotVerified, Inconclusive, ProviderError, Timeout];

    /// <summary>States a provider check may be run again from (the API decides, and answers 409 when it cannot).</summary>
    public static bool CanRetry(string? state) => state is ProviderNotConfigured or ProviderError or Timeout or Inconclusive;
}

/// <summary>Stage states (wire values of <c>StageState</c>).</summary>
public static class StageStates
{
    public const string NotStarted = "NotStarted";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>The few "may this button be shown" rules the pages share. Convenience only: the API enforces every one of them.</summary>
public static class VerificationActions
{
    public static bool CanCancel(string? status, string? type, Func<string, bool> has) =>
        VerificationStatuses.IsInProgress(status) && SubmitAllowed(type, has);

    /// <summary>
    /// A finished check whose official step did not give an answer. <paramref name="providerConfigured"/> false (known from the catalogue)
    /// hides the button for "not configured": there is nothing to retry until an administrator sets a provider up.
    /// </summary>
    public static bool CanRetryExternal(string? status, string? externalState, string? type, bool? providerConfigured, Func<string, bool> has, bool dataPurged = false)
    {
        if (dataPurged || status is not (VerificationStatuses.Completed or VerificationStatuses.ReviewRequired) || !ExternalStates.CanRetry(externalState) || !SubmitAllowed(type, has))
        {
            return false;
        }

        return externalState != ExternalStates.ProviderNotConfigured || providerConfigured == true;
    }

    private static bool SubmitAllowed(string? type, Func<string, bool> has) =>
        VerificationTypes.SubmitPermission(type) is { } permission && has(permission);
}
