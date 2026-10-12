using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Processing;

/// <summary>Pure helpers that turn a module's outcome into the rows we persist. No personal value is ever put into a check message here.</summary>
public static class ResultSupport
{
    public const string SubjectMacPurpose = "verification-subject";
    public const string ProviderFieldPrefix = "provider.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// The submitted-vs-extracted comparisons expressed as checks by the module processors (<c>ProcessorSupport.FieldCheck</c>): a
    /// consistency check on a named field whose message starts with the outcome name. Only the outcome travels on, never a value or a score.
    /// </summary>
    public static IReadOnlyList<FieldMatch> FieldMatchesFromChecks(IEnumerable<CheckResult> checks)
    {
        var matches = new List<FieldMatch>();
        foreach (var check in checks)
        {
            if (check.Stage != CheckStage.Consistency || check.Field is null || check.Field == "submitted"
                || check.Code.IndexOf("MATCH", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            var colon = check.Message.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && Enum.TryParse<FieldMatchOutcome>(check.Message[..colon], out var outcome))
            {
                matches.Add(new FieldMatch(check.Field, outcome));
            }
        }

        return matches;
    }

    public static string ToJson(IEnumerable<FieldMatch> matches) =>
        JsonSerializer.Serialize(matches.Select(m => new WireMatch(m.Field, m.Outcome)).ToList(), Json);

    public static IReadOnlyList<FieldMatch> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<WireMatch>>(json, Json) ?? []).Select(m => new FieldMatch(m.Field, m.Outcome)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IEnumerable<VerificationCheck> ToEntities(Guid clientId, Guid requestId, int firstOrdinal, IEnumerable<CheckResult> checks)
    {
        var ordinal = firstOrdinal;
        foreach (var c in checks)
        {
            yield return VerificationCheck.Create(clientId, requestId, ordinal++, c.Code, c.Stage, c.Outcome, c.Severity, c.Field, c.Message);
        }
    }

    /// <summary>The queryable derivatives of the extracted identifier: a masked form and a keyed hash. The full identifier stays in the sealed data.</summary>
    public static (MaskedIdentifier? Masked, byte[]? Hash) Identifier(ExtractedData? data, IPlatformCrypto crypto)
    {
        switch (data)
        {
            case PanData { PanNumber: { Length: > 0 } pan }:
                return (MaskedIdentifier.From(pan), crypto.ComputeMac(SubjectMacPurpose, Encoding.UTF8.GetBytes(Normalise(pan))));
            case PassportData { PassportNumber: { Length: > 0 } number }:
                return (MaskedIdentifier.From(number), crypto.ComputeMac(SubjectMacPurpose, Encoding.UTF8.GetBytes(Normalise(number))));
            case BankStatementData { AccountNumberMasked: { Length: > 0 } masked } bank:
                return (MaskedIdentifier.From(masked, 4), bank.AccountNumberHash);
            default:
                return (null, null);
        }
    }

    public static string Normalise(string identifier) => new(identifier.Where(char.IsLetterOrDigit).ToArray().Select(char.ToUpperInvariant).ToArray());

    /// <summary>PII-free check for the provider stage. The wording never says "genuine" or "valid": only Verified with a reference is positive (Rule V-1).</summary>
    public static CheckResult ExternalCheck(ExternalState state, bool unfunded = false)
    {
        if (unfunded)
        {
            return new CheckResult("EXTERNAL_VERIFICATION", CheckStage.External, CheckOutcome.NotEvaluated, CheckSeverity.Review, null,
                "The provider check was not performed because there were not enough credits.");
        }

        return state switch
        {
            ExternalState.Verified => new("EXTERNAL_VERIFICATION", CheckStage.External, CheckOutcome.Pass, CheckSeverity.Info, null, "The authoritative provider confirmed the details."),
            ExternalState.NotVerified => new("EXTERNAL_VERIFICATION", CheckStage.External, CheckOutcome.Fail, CheckSeverity.Review, null, "The authoritative provider could not confirm the details."),
            ExternalState.Inconclusive => new("EXTERNAL_VERIFICATION", CheckStage.External, CheckOutcome.Warn, CheckSeverity.Review, null, "The authoritative provider's answer was inconclusive."),
            ExternalState.ProviderError or ExternalState.Timeout => new("EXTERNAL_VERIFICATION", CheckStage.External, CheckOutcome.Warn, CheckSeverity.Review, null, "Official verification could not be completed. It can be retried."),
            ExternalState.ProviderNotConfigured => new("EXTERNAL_NOT_PERFORMED", CheckStage.External, CheckOutcome.NotApplicable, CheckSeverity.Info, null, "Official verification is not available: no authoritative provider is configured."),
            _ => new("EXTERNAL_NOT_PERFORMED", CheckStage.External, CheckOutcome.NotApplicable, CheckSeverity.Info, null, "No authoritative verification was performed."),
        };
    }

    private sealed record WireMatch(string Field, FieldMatchOutcome Outcome);
}
