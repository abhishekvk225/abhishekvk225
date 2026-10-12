namespace NexaVerify.Application.Verification;

/// <summary>
/// Audit action names of the module (docs/07 §10.8). Audit values carry ids, type, status and credit numbers only: never a document,
/// extracted value, identifier, name, amount or provider payload.
/// </summary>
public static class VerificationAuditActions
{
    public const string Submitted = "verification.submitted";
    public const string Completed = "verification.completed";
    public const string Failed = "verification.failed";
    public const string Cancelled = "verification.cancelled";
    public const string ExternalRetry = "verification.external_retry";
    public const string ReportDownloaded = "verification.report_downloaded";
    public const string DocumentDownloaded = "verification.document_downloaded";
    public const string Erased = "verification.erased";
    public const string ModuleEnabled = "verification.module_enabled";
    public const string ModuleDisabled = "verification.module_disabled";
    public const string AdminViewed = "verification.admin_viewed";

    public const string EntityType = "VerificationRequest";
    public const string ModuleEntityType = "VerificationModule";
}
