namespace NexaVerify.Domain.Verification;

/// <summary>Stable codes of the review reasons the domain itself derives (docs/07 §7.2). Module checks add their own codes.</summary>
public static class ReviewCodes
{
    public const string ProviderNotVerified = "PROVIDER_NOT_VERIFIED";
    public const string ProviderInconclusive = "PROVIDER_INCONCLUSIVE";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ProviderStageUnfunded = "PROVIDER_STAGE_UNFUNDED";

    internal static readonly string[] ProviderDerived = [ProviderNotVerified, ProviderInconclusive, ProviderUnavailable];
}

/// <summary>Domain error codes thrown as <see cref="Common.DomainException"/>.</summary>
public static class VerificationDomainErrors
{
    public const string InvalidTransition = "VERIFICATION_INVALID_TRANSITION";
    public const string VerifiedRequiresProvider = "VERIFIED_REQUIRES_PROVIDER";
    public const string ExternalResultFinal = "VERIFICATION_EXTERNAL_FINAL";
    public const string NotRetryable = "VERIFICATION_NOT_RETRYABLE";
    public const string NotCancellable = "VERIFICATION_NOT_CANCELLABLE";
    public const string InvalidState = "VERIFICATION_INVALID_STATE";
}

/// <summary>Pure rules that need no I/O: the allowed transitions and the computed assurance level.</summary>
public static class VerificationRules
{
    /// <summary>Request-level failure codes stored on the request (not HTTP codes), docs/07 §10.5.</summary>
    public static class FailureCodes
    {
        public const string DocumentUnreadable = "DOCUMENT_UNREADABLE";
        public const string OcrUnavailable = "OCR_UNAVAILABLE";
        public const string ProcessingFailed = "PROCESSING_FAILED";
        public const string ProcessingTimeout = "PROCESSING_TIMEOUT";
        public const string CreditsUnavailable = "CREDITS_UNAVAILABLE";
        public const string Cancelled = "CANCELLED";
        public const string Expired = "EXPIRED";
    }

    public static bool CanTransition(VerificationStatus from, VerificationStatus to) => (from, to) switch
    {
        (VerificationStatus.Queued, VerificationStatus.Processing) => true,
        (VerificationStatus.Queued, VerificationStatus.Cancelled) => true,
        (VerificationStatus.Queued, VerificationStatus.Expired) => true,
        (VerificationStatus.Processing, VerificationStatus.Queued) => true, // lease expired / retryable error / shutdown hand-back
        (VerificationStatus.Processing, VerificationStatus.Completed) => true,
        (VerificationStatus.Processing, VerificationStatus.ReviewRequired) => true,
        (VerificationStatus.Processing, VerificationStatus.Failed) => true,
        (VerificationStatus.Processing, VerificationStatus.Cancelled) => true,
        (VerificationStatus.Completed, VerificationStatus.Processing) => true, // retry-external (provider stage only)
        (VerificationStatus.ReviewRequired, VerificationStatus.Processing) => true,
        _ => false,
    };

    public static bool IsTerminal(VerificationStatus status) =>
        status is VerificationStatus.Completed or VerificationStatus.ReviewRequired or VerificationStatus.Failed or VerificationStatus.Cancelled or VerificationStatus.Expired;

    /// <summary>States from which the authoritative stage may be (re-)run. Verified and NotVerified are definitive answers and are never re-run.</summary>
    public static bool IsRetryable(ExternalState state) =>
        state is ExternalState.ProviderNotConfigured or ExternalState.ProviderError or ExternalState.Timeout or ExternalState.Inconclusive;

    /// <summary>
    /// Rule V-1: only an authoritative provider answer of <see cref="ExternalState.Verified"/> with a provider id and reference yields
    /// <see cref="AssuranceLevel.AuthoritativelyVerified"/>. OCR, format checks, MRZ check digits and consistency checks never do.
    /// </summary>
    public static AssuranceLevel ComputeAssurance(
        StageState extracted, StageState rules, StageState consistency, ExternalState external, string? providerId, string? providerReference)
    {
        if (external == ExternalState.Verified && !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(providerReference))
        {
            return AssuranceLevel.AuthoritativelyVerified;
        }

        if (rules == StageState.Completed && consistency == StageState.Completed)
        {
            return AssuranceLevel.ChecksCompleted;
        }

        return extracted == StageState.Completed || rules == StageState.Completed
            ? AssuranceLevel.ExtractedOnly
            : AssuranceLevel.None;
    }
}
