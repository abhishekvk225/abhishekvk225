namespace NexaVerify.Contracts.Common;

/// <summary>Stable machine-readable error codes returned in ProblemDetails.code (see docs/03 §1).</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ImageInvalid = "IMAGE_INVALID";
    public const string ImageTooLarge = "IMAGE_TOO_LARGE";
    public const string ImageUnsupportedType = "IMAGE_UNSUPPORTED_TYPE";

    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string TokenExpired = "TOKEN_EXPIRED";
    public const string ApiKeyInvalid = "API_KEY_INVALID";
    public const string ApiKeyExpired = "API_KEY_EXPIRED";
    public const string ApiKeyRevoked = "API_KEY_REVOKED";

    public const string LicenseNotFound = "LICENSE_NOT_FOUND";
    public const string LicenseExpired = "LICENSE_EXPIRED";
    public const string LicenseInsufficientBalance = "LICENSE_INSUFFICIENT_BALANCE";
    public const string LicenseSuspended = "LICENSE_SUSPENDED";
    public const string LicenseInvalidTransition = "LICENSE_INVALID_TRANSITION";

    public const string Forbidden = "FORBIDDEN";
    public const string ClientSuspended = "CLIENT_SUSPENDED";
    public const string ClientInactive = "CLIENT_INACTIVE";
    public const string IpNotAllowed = "IP_NOT_ALLOWED";

    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string DuplicateExternalRef = "DUPLICATE_EXTERNAL_REF";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";

    public const string NoFaceDetected = "NO_FACE_DETECTED";
    public const string MultipleFaces = "MULTIPLE_FACES";
    public const string LowQualityImage = "LOW_QUALITY_IMAGE";

    public const string RateLimited = "RATE_LIMITED";
    public const string DailyQuotaExceeded = "DAILY_QUOTA_EXCEEDED";

    public const string InternalError = "INTERNAL_ERROR";
    public const string FaceProviderUnavailable = "FACE_PROVIDER_UNAVAILABLE";
}
