namespace NexaVerify.Domain.Verification;

/// <summary>Document verification kinds shipped in release 1. Upcoming modules deliberately have no member here (docs/07 §3).</summary>
public enum VerificationType
{
    BankStatement = 0,
    Pan,
    Passport,
}

public enum VerificationStatus
{
    Queued = 0,
    Processing,
    Completed,
    ReviewRequired,
    Failed,
    Cancelled,
    Expired,
}

/// <summary><c>Completed</c> means the stage RAN, not that everything in it passed (individual checks tell that).</summary>
public enum StageState
{
    NotStarted = 0,
    Completed,
    Failed,
    Skipped,
}

/// <summary>Authoritative (provider) verification only. Only <see cref="Verified"/> with a provider id and reference is ever shown as verified.</summary>
public enum ExternalState
{
    NotRequested = 0,
    ProviderNotConfigured,
    Pending,
    Verified,
    NotVerified,
    Inconclusive,
    ProviderError,
    Timeout,
}

public enum CheckOutcome
{
    Pass = 0,
    Warn,
    Fail,
    NotApplicable,
    NotEvaluated,
}

public enum CheckSeverity
{
    Info = 0,
    Warning,
    Review,
}

public enum CheckStage
{
    Extraction = 0,
    Rules,
    Consistency,
    External,
}

public enum FieldMatchOutcome
{
    Match = 0,
    PartialMatch,
    Mismatch,
    NotProvided,
    NotExtracted,
}

/// <summary>Computed, never stored as an input. Only <see cref="AuthoritativelyVerified"/> may be described as verified.</summary>
public enum AssuranceLevel
{
    None = 0,
    ExtractedOnly,
    ChecksCompleted,
    AuthoritativelyVerified,
}

public enum DocumentKind
{
    Pdf = 0,
    Jpeg,
    Png,
    WebP,
}

public enum SubmitSource
{
    Portal = 0,
    Api,
}

/// <summary>The two billable stages of a verification (docs/07 §8).</summary>
public enum VerificationStage
{
    Extraction = 0,
    Provider,
}

public enum VerificationActorType
{
    System = 0,
    User,
    ApiKey,
    Platform,
}

public enum DownloadKind
{
    Report = 0,
    TransactionsCsv,
    OriginalDocument,
}
