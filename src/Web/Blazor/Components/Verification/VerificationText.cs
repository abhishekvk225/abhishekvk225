using System.Globalization;
using System.Text.RegularExpressions;
using NexaVerify.Contracts.Verification;
using NexaVerify.Web.Services;

namespace NexaVerify.Web.Components.Verification;

/// <summary>
/// The one place that words what a document check means. Nothing here ever calls a document "authentic", "genuine" or "valid": the only
/// positive claim is "Officially verified by {provider}", and only when the provider's own answer was Verified (docs/07 Rule V-1). The
/// rest of the UI takes its sentences from this table, and a test scans it for forbidden words.
/// </summary>
public static partial class VerificationText
{
    /// <summary>Shown wherever a result is shown. Honest about what the checks can and cannot tell.</summary>
    public const string Disclaimer =
        "We read the document and look for mistakes and inconsistencies. These checks cannot show that a document is real, or that it was issued to the person named on it. " +
        "Only an official check by a named provider can confirm details, and only when one is set up for your account.";

    /// <summary>Short form for the top of each submission page.</summary>
    public const string SubmitPurposeNote =
        "This reads the document and runs consistency checks. It does not prove the document is real. Official confirmation needs an approved provider to be set up for your account.";

    public const string UpcomingNote = "Not available yet. No credits are used.";

    // ---- statuses -------------------------------------------------------------------------------------------------------------

    public static string StatusLabel(string? status) => status switch
    {
        VerificationStatuses.Queued => "Queued",
        VerificationStatuses.Processing => "Processing",
        VerificationStatuses.Completed => "Checks completed",
        VerificationStatuses.ReviewRequired => "Needs review",
        VerificationStatuses.Failed => "Failed",
        VerificationStatuses.Cancelled => "Cancelled",
        VerificationStatuses.Expired => "Expired",
        _ => Humanize(status),
    };

    /// <summary>One plain sentence for the top of the details page.</summary>
    public static string Headline(string? status, string? externalState, string? providerId, string? assurance)
    {
        if (externalState == ExternalStates.Verified && assurance == "AuthoritativelyVerified")
        {
            return ExternalHeadline(externalState, providerId);
        }

        return status switch
        {
            VerificationStatuses.Queued => "Waiting to start",
            VerificationStatuses.Processing => "Checking your document",
            VerificationStatuses.Completed => "Checks completed. Not officially verified.",
            VerificationStatuses.ReviewRequired => "Needs review. Not officially verified.",
            VerificationStatuses.Failed => "The check could not be completed",
            VerificationStatuses.Cancelled => "This check was cancelled",
            VerificationStatuses.Expired => "This check expired before it started",
            _ => StatusLabel(status),
        };
    }

    // ---- official (provider) check ------------------------------------------------------------------------------------------

    public static string ExternalLabel(string? state) => state switch
    {
        ExternalStates.NotRequested => "Not requested",
        ExternalStates.ProviderNotConfigured => "Not available",
        ExternalStates.Pending => "Waiting for provider",
        ExternalStates.Verified => "Verified",
        ExternalStates.NotVerified => "Not confirmed",
        ExternalStates.Inconclusive => "Inconclusive",
        ExternalStates.ProviderError => "Provider problem",
        ExternalStates.Timeout => "Timed out",
        _ => Humanize(state),
    };

    public static string ExternalHeadline(string? state, string? providerId) => state switch
    {
        ExternalStates.Verified => string.IsNullOrWhiteSpace(providerId) ? "Officially verified" : $"Officially verified by {providerId}",
        ExternalStates.ProviderNotConfigured => "Official verification is not available for your account",
        ExternalStates.NotVerified => "The provider could not confirm these details",
        ExternalStates.Inconclusive => "The provider's answer was inconclusive",
        ExternalStates.ProviderError or ExternalStates.Timeout => "Official verification could not be completed. You can retry.",
        ExternalStates.Pending => "Waiting for the provider's answer",
        _ => "No official verification was requested",
    };

    public static string ExternalExplanation(string? state, string type) => state switch
    {
        ExternalStates.Verified => "An approved provider confirmed the details. The reference below identifies their answer.",
        ExternalStates.ProviderNotConfigured => "No approved provider is set up for this kind of check. The document was read and checked for consistency, but nothing was confirmed with an official source. Ask your administrator or NexaVerify support when official checks will be available.",
        ExternalStates.NotVerified => "The provider answered that the details do not match their records. This is a definite answer, not a technical problem. Review the details with the person.",
        ExternalStates.Inconclusive => "The provider could not give a clear yes or no. You can try again later, or review the details by hand.",
        ExternalStates.ProviderError => "The provider could not be reached or reported a problem. Nothing was decided. You can try again, and you are only charged once the provider gives an answer.",
        ExternalStates.Timeout => "The provider did not answer in time. Nothing was decided. You can try again, and you are only charged once the provider gives an answer.",
        ExternalStates.Pending => "The provider has been asked. This page updates by itself when the answer arrives.",
        _ => type == VerificationTypes.BankStatement
            ? "There is no official source to confirm a bank statement with, so only the reading and consistency checks are shown."
            : "No official check was requested for this one.",
    };

    // ---- stages ---------------------------------------------------------------------------------------------------------------

    public static string StageStateLabel(string? state) => state switch
    {
        StageStates.NotStarted => "Not started",
        StageStates.Completed => "Done",
        StageStates.Failed => "Failed",
        StageStates.Skipped => "Skipped",
        _ => Humanize(state),
    };

    public static string StageStateHelp(string step, string? state)
    {
        if (state == StageStates.Skipped)
        {
            return step == "Uploaded" ? "No file was uploaded." : "Not needed for this check.";
        }

        return step switch
        {
            "Uploaded" => "Your file was received.",
            "Extracted" => "Text and details were read from the document.",
            "Rules checked" => "Format and business rules were applied.",
            "Consistency checked" => "Details were compared with each other and with what you entered.",
            _ => string.Empty,
        };
    }

    // ---- checks ---------------------------------------------------------------------------------------------------------------

    public static string CheckOutcomeLabel(string? outcome) => outcome switch
    {
        "Pass" => "Passed",
        "Warn" => "Warning",
        "Fail" => "Failed",
        "NotApplicable" => "Not applicable",
        "NotEvaluated" => "Not checked",
        _ => Humanize(outcome),
    };

    public static string SeverityLabel(string? severity) => severity switch
    {
        "Info" => "For information",
        "Warning" => "Worth a look",
        "Review" => "Needs review",
        _ => Humanize(severity),
    };

    public static string MrzLabel(string? state) => state switch
    {
        "Valid" => "Passed",
        "Invalid" => "Failed",
        "NotPresent" => "Not present",
        _ => Humanize(state),
    };

    /// <summary>Plain-language name of a check. Unknown codes fall back to the API's own (personal-data-free) sentence.</summary>
    public static string CheckTitle(string code, string? apiMessage) => CheckTitles.TryGetValue(code, out var title)
        ? title
        : !string.IsNullOrWhiteSpace(apiMessage) ? apiMessage : Humanize(code);

    private static readonly Dictionary<string, string> CheckTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BS_COMPLETENESS"] = "Key details are present",
        ["BS_PERIOD_VALID"] = "Statement period makes sense",
        ["BS_ROW_SHAPE"] = "Each row has a date and one amount",
        ["BS_TX_IN_PERIOD"] = "Transactions fall inside the statement period",
        ["BS_TX_ORDER"] = "Transactions are in date order",
        ["BS_ROW_ARITHMETIC"] = "Running balance adds up row by row",
        ["BS_OPENING_CONTINUITY"] = "Opening balance matches the first row",
        ["BS_CLOSING_CONTINUITY"] = "Closing balance matches the last row",
        ["BS_TOTALS"] = "Opening balance, credits and debits add up to the closing balance",
        ["BS_DUPLICATES"] = "Repeated rows",
        ["BS_SUBMITTED_MATCH"] = "Details match what you entered",
        ["BS_EXTRACTION_CONFIDENCE"] = "How clearly the text could be read",
        ["BS_EXTERNAL_NOT_PERFORMED"] = "No official check exists for bank statements",
        ["PAN_FORMAT"] = "PAN has the expected format",
        ["PAN_HOLDER_TYPE"] = "Holder type letter is recognised",
        ["PAN_NAME_INITIAL"] = "Fifth letter fits the name",
        ["PAN_DOC_NUMBER_MATCH"] = "Number on the document matches the number you entered",
        ["PAN_NAME_MATCH"] = "Name matches what you entered",
        ["PAN_DOB_MATCH"] = "Date of birth matches what you entered",
        ["PAN_FATHER_NAME_MATCH"] = "Father's name matches what you entered",
        ["PAN_EXTRACTION_CONFIDENCE"] = "How clearly the text could be read",
        ["PAN_OCR_CORRECTED"] = "Some characters were corrected after reading",
        ["PP_MRZ_PRESENT"] = "Machine-readable zone was found",
        ["PP_MRZ_CHECK_DOC"] = "Passport number check digit",
        ["PP_MRZ_CHECK_DOB"] = "Date of birth check digit",
        ["PP_MRZ_CHECK_EXP"] = "Expiry date check digit",
        ["PP_MRZ_CHECK_PERSONAL"] = "Personal number check digit",
        ["PP_MRZ_CHECK_COMPOSITE"] = "Overall check digit",
        ["PP_NUMBER_FORMAT"] = "Passport number has the expected format",
        ["PP_DATES_VALID"] = "Dates are real calendar dates",
        ["PP_NOT_EXPIRED"] = "Passport has not expired",
        ["PP_ISSUE_BEFORE_EXPIRY"] = "Issue date is before the expiry date",
        ["PP_VALIDITY_SPAN"] = "Length of time the passport covers",
        ["PP_AGE_PLAUSIBLE"] = "Age fits the date of birth",
        ["PP_VIZ_MRZ_CONSISTENT"] = "Printed details agree with the machine-readable zone",
        ["PP_EXTRACTION_CONFIDENCE"] = "How clearly the text could be read",
        ["MRZ_OCR_CORRECTED"] = "Some characters were corrected after reading",
        ["MRZ_FORMAT_UNSUPPORTED"] = "This machine-readable zone layout is not supported yet",
        ["EXTERNAL_NOT_PERFORMED"] = "No official check was performed",
    };

    // ---- field matches --------------------------------------------------------------------------------------------------------

    public static string MatchLabel(string? outcome) => outcome switch
    {
        "Match" => "Matches",
        "PartialMatch" => "Partly matches",
        "Mismatch" => "Does not match",
        "NotProvided" => "Not entered",
        "NotExtracted" => "Not read from the document",
        _ => Humanize(outcome),
    };

    public static string FieldLabel(string? field) => (field ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "name" or "fullname" or "holdername" or "accountholdername" or "nameoncard" => "Name",
        "fathername" => "Father's name",
        "pan" or "pannumber" => "PAN",
        "dateofbirth" or "dob" => "Date of birth",
        "passportnumber" => "Passport number",
        "nationality" => "Nationality",
        "dateofexpiry" or "expirydate" => "Expiry date",
        "dateofissue" or "issuedate" => "Issue date",
        "bankname" => "Bank name",
        "accountnumberlast4" or "accountnumber" => "Account number (last 4)",
        "periodfrom" => "Period start",
        "periodto" => "Period end",
        "period" => "Statement period",
        _ => Humanize(field),
    };

    // ---- failures -------------------------------------------------------------------------------------------------------------

    /// <summary>Why a request ended without a result (the codes stored on the request).</summary>
    public static string FailureMessage(string? errorCode, string? apiMessage) => errorCode switch
    {
        "DOCUMENT_UNREADABLE" => "We could not read the text in this document. Try a clearer scan or a digital copy of the document.",
        "OCR_UNAVAILABLE" => "Reading scanned documents is not available right now. Try a digital PDF, or try again later.",
        "PROCESSING_FAILED" => "Something went wrong while checking the document. You were not charged. Please try again.",
        "PROCESSING_TIMEOUT" => "The check took too long and was stopped. You were not charged. Please try again.",
        "CREDITS_UNAVAILABLE" => "Your credits ran out before the check could finish, so no result was kept and nothing was charged. Add credits and try again.",
        "CANCELLED" => "This check was cancelled.",
        "EXPIRED" => "The check waited too long to start and was closed. You were not charged.",
        _ => !string.IsNullOrWhiteSpace(apiMessage) ? apiMessage : "The check could not be completed.",
    };

    /// <summary>What went wrong with a submit, in words a person can act on.</summary>
    public static string SubmitProblem(ApiError error) => error.Code switch
    {
        "DOCUMENT_REQUIRED" => "Add a document first.",
        "DOCUMENT_INVALID" => "We could not read that file. It may be damaged. Try saving or scanning it again.",
        "DOCUMENT_UNSUPPORTED_TYPE" => "That kind of file is not accepted. Use a PDF, JPEG, PNG or WebP file.",
        "DOCUMENT_TOO_LARGE" or "PAYLOAD_TOO_LARGE" => "That file is too large. Use a smaller file or a lower scan resolution.",
        "DOCUMENT_TOO_MANY_PAGES" => "That PDF has more pages than this check accepts. Send only the pages you need.",
        "DOCUMENT_ENCRYPTED" => "That PDF is password protected. Remove the password and try again.",
        "DOCUMENT_ACTIVE_CONTENT" => "That PDF contains scripts or attachments, so it cannot be accepted. Save a plain copy and try again.",
        "VERIFICATION_MODULE_DISABLED" => "This check is switched off right now.",
        "VERIFICATION_QUEUE_FULL" => "You already have many checks waiting. Wait for some to finish, then try again.",
        "PROCESSING_UNAVAILABLE" => "The service is busy right now. Please try again in a moment.",
        _ => error.Message,
    };

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    public static string Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var spaced = Words().Replace(value.Replace('_', ' '), " ").Trim();
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    /// <summary>"INR 12,345.60" style amount with exact decimal arithmetic through the shared money helper.</summary>
    public static string Amount(decimal? amount, string? currency)
    {
        if (amount is null)
        {
            return "—";
        }

        var exponent = Money.Exponent(currency);
        var minor = decimal.Round(amount.Value * (decimal)Math.Pow(10, exponent), 0, MidpointRounding.AwayFromZero);
        return Money.Format((long)minor, currency);
    }

    public static string Day(DateOnly? date) => date is null ? "—" : date.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string When(DateTime? value) => value is null ? "—" : value.Value.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Last eight characters of the request id (the first ones are a timestamp), enough to quote to support; the full id is always available.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[^8..];

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
