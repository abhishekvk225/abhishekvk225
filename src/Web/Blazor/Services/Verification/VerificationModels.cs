using NexaVerify.Contracts.Verification;

namespace NexaVerify.Web.Services;

/// <summary>
/// A document chosen for upload. Held in memory by the page only until the request is done, then overwritten. The original file name is
/// never sent to the API (it may contain a person's name); only the detected content type travels with the bytes.
/// </summary>
public sealed record DocumentUploadFile(byte[] Data, string ContentType, string FileName)
{
    /// <summary>Overwrites the bytes. The portal never keeps documents.</summary>
    public void Wipe() => Array.Clear(Data);
}

/// <summary>
/// One catalogue entry. All fields of <see cref="VerificationModuleDto"/> plus the credit hints the confirmation dialog would like to
/// show. The API does not send the hints yet (listed as a contract wish): they stay null until it does, and the dialog then says so.
/// </summary>
public sealed record VerificationModuleInfo(
    string Key,
    string DisplayName,
    string Description,
    string Status,
    bool Available,
    IReadOnlyList<string> RequiredPermissions,
    bool? ProviderConfigured,
    VerificationLimitsDto? Limits,
    string? PlannedNote,
    int? ExtractionCredits = null,
    int? ProviderCredits = null)
{
    public bool IsUpcoming => string.Equals(Status, VerificationModuleStatuses.Upcoming, StringComparison.OrdinalIgnoreCase);

    public bool IsDisabled => string.Equals(Status, VerificationModuleStatuses.Disabled, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of asking the API for a short-lived download link and opening it (used by the portal's relay endpoints only).</summary>
public sealed record VerificationDownloadKind(string Kind, string? Format, string ContentType, string Extension)
{
    public static VerificationDownloadKind ReportHtml { get; } = new(VerificationDownloadKinds.Report, VerificationDownloadKinds.Html, "text/html", "html");

    public static VerificationDownloadKind ReportJson { get; } = new(VerificationDownloadKinds.Report, VerificationDownloadKinds.Json, "application/json", "json");

    public static VerificationDownloadKind TransactionsCsv { get; } = new(VerificationDownloadKinds.TransactionsCsv, null, "text/csv", "csv");
}
