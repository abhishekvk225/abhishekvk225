using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Processing;

/// <summary>
/// Document verification configuration (<c>Verification:*</c>, docs/07 §5). Every limit is configuration; none is a literal in code.
/// Validated at start-up by <see cref="VerificationOptionsValidator"/>. Provider credentials do NOT live here (secret files / env only).
/// </summary>
public sealed class VerificationOptions
{
    public const string SectionName = "Verification";

    public UploadOptions Upload { get; set; } = new();

    public LimitsOptions Limits { get; set; } = new();

    public WorkerOptions Worker { get; set; } = new();

    public ProcessingOptions Processing { get; set; } = new();

    public OcrOptions Ocr { get; set; } = new();

    public BankStatementOptions BankStatement { get; set; } = new();

    public PanRuleOptions Pan { get; set; } = new();

    public PassportOptions Passport { get; set; } = new();

    public NameOptions Names { get; set; } = new();

    public ProvidersOptions Providers { get; set; } = new();

    public bool RequireConsentReference { get; set; } = true;

    public RetentionOptions Retention { get; set; } = new();

    public ResultOptions Result { get; set; } = new();

    public DownloadOptions Download { get; set; } = new();

    public UploadPolicy UploadPolicyFor(VerificationType type) => new(
        Upload.MaxFileBytes, Upload.AllowedExtensions, Upload.MaxPages.For(type), Upload.MaxImagePixels, Upload.RejectActiveContent);
}

public sealed class UploadOptions
{
    /// <summary>Per file. Also drives the per-route request size limit (this + <see cref="RequestOverheadBytes"/>).</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    public long RequestOverheadBytes { get; set; } = 1024 * 1024;

    public string[] AllowedExtensions { get; set; } = [".pdf", ".jpg", ".jpeg", ".png", ".webp"];

    public MaxPagesOptions MaxPages { get; set; } = new();

    public int MaxImagePixels { get; set; } = 40_000_000;

    public bool RejectActiveContent { get; set; } = true;

    public int InspectTimeoutSeconds { get; set; } = 5;

    public int InspectMaxConcurrent { get; set; } = 4;
}

public sealed class MaxPagesOptions
{
    public int BankStatement { get; set; } = 30;

    public int Pan { get; set; } = 2;

    public int Passport { get; set; } = 4;

    public int For(VerificationType type) => type switch
    {
        VerificationType.BankStatement => BankStatement,
        VerificationType.Pan => Pan,
        VerificationType.Passport => Passport,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public sealed class LimitsOptions
{
    /// <summary>Queued + Processing requests per client; over this the submit answers 429 VERIFICATION_QUEUE_FULL.</summary>
    public int MaxQueuedPerClient { get; set; } = 50;
}

public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;

    public int Parallelism { get; set; } = 2;

    public int PollSeconds { get; set; } = 2;

    public int BatchSize { get; set; } = 5;

    /// <summary>Must exceed <c>Processing:TimeoutSeconds</c> + 30.</summary>
    public int LeaseSeconds { get; set; } = 300;
}

public sealed class ProcessingOptions
{
    /// <summary>Document-stage budget.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    public int MaxAttempts { get; set; } = 3;

    /// <summary>Delay before attempt n+1 (index n-1; the last value repeats).</summary>
    public int[] BackoffSeconds { get; set; } = [30, 120, 600];

    /// <summary>Older Queued requests become Expired (never charged).</summary>
    public int MaxQueueAgeMinutes { get; set; } = 60;
}

public sealed class OcrOptions
{
    /// <summary><c>tesseract-cli</c> or <c>none</c>.</summary>
    public string Provider { get; set; } = "tesseract-cli";

    public TesseractOptions Tesseract { get; set; } = new();

    public int Dpi { get; set; } = 300;

    /// <summary>Below this a PDF page is treated as scanned and rasterised.</summary>
    public int MinTextCharsPerPage { get; set; } = 40;

    /// <summary>Below this the mean OCR confidence raises a review signal, not a failure.</summary>
    public decimal MinMeanConfidence { get; set; } = 0.60m;
}

public sealed class TesseractOptions
{
    public string ExecutablePath { get; set; } = "tesseract";

    public string? TessdataPath { get; set; }

    public string Languages { get; set; } = "eng";

    public int TimeoutSecondsPerPage { get; set; } = 30;

    public int MaxConcurrent { get; set; } = 2;
}

public sealed class BankStatementOptions
{
    /// <summary>Directory of <c>*.json</c> rule sets; invalid files fail start-up.</summary>
    public string RuleSetsPath { get; set; } = "config/bank-rules";

    public decimal BalanceTolerance { get; set; } = 0.01m;

    public int MaxTransactions { get; set; } = 5000;

    public int MaxPeriodDays { get; set; } = 400;

    public string DefaultCurrency { get; set; } = "INR";
}

/// <summary>PAN rules (<c>Verification:Pan:*</c>). Jurisdiction-appropriate and configurable.</summary>
public sealed class PanRuleOptions
{
    public string Pattern { get; set; } = "^[A-Z]{5}[0-9]{4}[A-Z]$";

    /// <summary>Allowed 4th characters (holder type table). Confirm against current Income Tax Department documentation before go-live.</summary>
    public string[] HolderTypes { get; set; } = ["A", "B", "C", "F", "G", "H", "L", "J", "P", "T"];
}

public sealed class PassportOptions
{
    /// <summary>Pattern per ISO 3166 alpha-3 country, for example <c>IND</c>.</summary>
    public Dictionary<string, string> NumberPatterns { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["IND"] = "^[A-Z][0-9]{7}$" };

    public string DefaultNumberPattern { get; set; } = "^[A-Z0-9]{6,9}$";

    /// <summary>Validity longer than this raises a warning.</summary>
    public int MaxValidityYears { get; set; } = 10;
}

public sealed class NameOptions
{
    public decimal MatchThreshold { get; set; } = 0.90m;

    public decimal PartialThreshold { get; set; } = 0.75m;

    public string[] IgnoredTokens { get; set; } = ["MR", "MRS", "MS", "DR", "SHRI", "SMT", "KUMARI", "MISS"];
}

public sealed class ProvidersOptions
{
    public ProviderOptions Pan { get; set; } = new();

    public ProviderOptions Passport { get; set; } = new();

    /// <summary>Raw provider payloads contain personal data; release 1 requires false.</summary>
    public bool StoreRawResponse { get; set; }
}

public sealed class ProviderOptions
{
    public const string None = "None";

    /// <summary><c>None</c> or a vendor id known to the DI registration. <c>None</c> yields ProviderNotConfigured.</summary>
    public string Provider { get; set; } = None;

    public int TimeoutSeconds { get; set; } = 10;

    public int MaxAttempts { get; set; } = 3;

    public int BreakerFailures { get; set; } = 5;

    public int BreakerOpenSeconds { get; set; } = 60;
}

public sealed class RetentionOptions
{
    /// <summary>Hard cap 90.</summary>
    public int DocumentDays { get; set; } = 7;

    /// <summary>Hard cap 365.</summary>
    public int ResultDays { get; set; } = 90;

    public int MetadataDays { get; set; } = 730;

    public int PersonalMetadataDays { get; set; } = 90;

    public const int MaxDocumentDays = 90;
    public const int MaxResultDays = 365;
}

public sealed class ResultOptions
{
    public int InlineTransactions { get; set; } = 500;

    public bool IncludeExtractedPersonalFields { get; set; } = true;
}

public sealed class DownloadOptions
{
    public int LinkSeconds { get; set; } = 60;

    /// <summary>Original-document download is off by default.</summary>
    public bool AllowOriginalDownload { get; set; }
}
