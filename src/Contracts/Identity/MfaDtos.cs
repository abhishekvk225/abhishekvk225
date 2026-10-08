using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Identity;

/// <summary>Shown ONCE when enrolment starts: the shared secret and the otpauth URI an authenticator app (or QR code) consumes.</summary>
public sealed record MfaEnrolmentDto(
    [property: Sensitive] string SecretBase32,
    [property: Sensitive] string OtpauthUri,
    string Issuer,
    string AccountName,
    string Algorithm,
    int Digits,
    int PeriodSeconds);

public sealed record ConfirmMfaRequest([property: Sensitive] string Code);

/// <summary>Enrolment finished: the recovery codes (shown once) and a fresh session, because turning MFA on ends the earlier sessions.</summary>
public sealed record MfaEnabledDto([property: Sensitive] IReadOnlyList<string> RecoveryCodes, LoginResponse Session);

public sealed record RegenerateRecoveryCodesRequest([property: Sensitive] string Code);

public sealed record RecoveryCodesDto([property: Sensitive] IReadOnlyList<string> RecoveryCodes);

/// <summary>Second sign-in step. <c>Code</c> is the 6-digit authenticator code or one recovery code.</summary>
public sealed record VerifyMfaRequest([property: Sensitive] string ChallengeToken, [property: Sensitive] string Code);

public sealed record MfaStatusDto(bool Available, bool Enabled, bool EnrolmentRequired, bool EnrolmentPending, int RecoveryCodesRemaining);

/// <summary>Another SuperAdmin switches a user's MFA off (lost device). The reason is mandatory and audited.</summary>
public sealed record ResetMfaRequest(string Reason);
