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
    public const string ApiAccessDisabled = "API_ACCESS_DISABLED";
    public const string MfaRequired = "MFA_REQUIRED";
    public const string MfaCodeInvalid = "MFA_CODE_INVALID";
    public const string MfaChallengeInvalid = "MFA_CHALLENGE_INVALID";
    public const string ApprovalRequired = "APPROVAL_REQUIRED";
    public const string ApprovalExpired = "APPROVAL_EXPIRED";
    public const string ApprovalNotPending = "APPROVAL_NOT_PENDING";
    public const string SelfApprovalForbidden = "SELF_APPROVAL_FORBIDDEN";

    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string DuplicateExternalRef = "DUPLICATE_EXTERNAL_REF";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";

    public const string SignupDisabled = "SIGNUP_DISABLED";
    public const string CaptchaFailed = "CAPTCHA_FAILED";

    public const string BillingDisabled = "BILLING_DISABLED";
    public const string BillingProfileIncomplete = "BILLING_PROFILE_INCOMPLETE";
    public const string PaymentProviderUnavailable = "PAYMENT_PROVIDER_UNAVAILABLE";
    public const string OrderNotRefundable = "ORDER_NOT_REFUNDABLE";

    public const string NoFaceDetected = "NO_FACE_DETECTED";
    public const string MultipleFaces = "MULTIPLE_FACES";
    public const string LowQualityImage = "LOW_QUALITY_IMAGE";

    public const string RateLimited = "RATE_LIMITED";
    public const string DailyQuotaExceeded = "DAILY_QUOTA_EXCEEDED";

    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RequestTimeout = "REQUEST_TIMEOUT";

    // M14 document verification (docs/07 §10.5)
    public const string DocumentRequired = "DOCUMENT_REQUIRED";
    public const string DocumentInvalid = "DOCUMENT_INVALID";
    public const string DocumentUnsupportedType = "DOCUMENT_UNSUPPORTED_TYPE";
    public const string DocumentTooLarge = "DOCUMENT_TOO_LARGE";
    public const string DocumentTooManyPages = "DOCUMENT_TOO_MANY_PAGES";
    public const string DocumentEncrypted = "DOCUMENT_ENCRYPTED";
    public const string DocumentActiveContent = "DOCUMENT_ACTIVE_CONTENT";
    public const string VerificationModuleDisabled = "VERIFICATION_MODULE_DISABLED";
    public const string VerificationNotCancellable = "VERIFICATION_NOT_CANCELLABLE";
    public const string VerificationNotRetryable = "VERIFICATION_NOT_RETRYABLE";
    public const string VerificationQueueFull = "VERIFICATION_QUEUE_FULL";
    public const string DownloadLinkExpired = "DOWNLOAD_LINK_EXPIRED";
    public const string ProcessingUnavailable = "PROCESSING_UNAVAILABLE";

    // Request-level failure codes stored on a verification (not HTTP errors)
    public const string DocumentUnreadable = "DOCUMENT_UNREADABLE";
    public const string OcrUnavailable = "OCR_UNAVAILABLE";
    public const string ProcessingFailed = "PROCESSING_FAILED";
    public const string ProcessingTimeout = "PROCESSING_TIMEOUT";
    public const string CreditsUnavailable = "CREDITS_UNAVAILABLE";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";

    public const string InternalError = "INTERNAL_ERROR";
    public const string FaceProviderUnavailable = "FACE_PROVIDER_UNAVAILABLE";
}
