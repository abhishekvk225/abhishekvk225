namespace NexaVerify.Domain.Licensing;

public enum LicenseStatus
{
    Draft = 0,
    Active,
    Inactive,
    Suspended,
    Expired,
    Revoked,
}

/// <summary>What a ledger row records. <c>Credits</c> is always the signed change of the license's REMAINING balance.</summary>
public enum LedgerEntryType
{
    Grant = 0,
    Consume,
    Refund,
    Adjustment,
    Renewal,
    ExpiryWriteOff,
    Revocation,
}

/// <summary>Billable operations. Mirrors the face-recognition operations.</summary>
public enum MeteredOperation
{
    Enroll = 0,
    Verify,
    Identify,
    Detect,
}

/// <summary>When a metered operation is charged.</summary>
public enum ChargePolicy
{
    /// <summary>Whenever the engine produced a definitive answer, including "no match" (default).</summary>
    OnCompleted = 0,

    /// <summary>Only for positive results (enrolled / matched).</summary>
    OnSuccess,

    /// <summary>Every attempt, even failed ones.</summary>
    OnAttempt,
}

/// <summary>Result of a metered operation, as far as billing cares.</summary>
public enum MeterOutcome
{
    /// <summary>Enrolled / matched.</summary>
    Success = 0,

    /// <summary>Definitive negative answer (no match).</summary>
    NoMatch,

    /// <summary>Not a definitive answer (no face, low quality, provider error, rejected).</summary>
    Failed,
}

public enum CostRuleScope
{
    PlatformDefault = 0,
    Plan,
    Client,
}
