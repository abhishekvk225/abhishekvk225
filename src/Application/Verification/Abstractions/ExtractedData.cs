using System.Text.Json.Serialization;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Abstractions;

/// <summary>
/// Extracted document data. A CLOSED set (no polymorphic deserialisation of arbitrary types). Full identifiers live only here, which is
/// stored encrypted; <see cref="object.ToString"/> is sealed so a record can never leak values into a log line.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(BankStatementData), "bank-statement")]
[JsonDerivedType(typeof(PanData), "pan")]
[JsonDerivedType(typeof(PassportData), "passport")]
public abstract record ExtractedData
{
    public int SchemaVersion { get; init; } = 1;

    public sealed override string ToString() => GetType().Name;
}

public enum TextSource
{
    DigitalPdf = 0,
    Ocr,
    Mixed,
}

public sealed record BankTransaction(
    int Index, DateOnly Date, DateOnly? ValueDate, string Description, string? Reference, decimal? Debit, decimal? Credit, decimal? Balance, decimal? Confidence);

public sealed record BankStatementData : ExtractedData
{
    public string RuleSetId { get; init; } = "generic";

    public string? BankName { get; init; }

    public string? AccountHolderName { get; init; }

    /// <summary>For example <c>XXXXXX1234</c>. The full number is never stored.</summary>
    public string? AccountNumberMasked { get; init; }

    /// <summary>Keyed MAC of the account number (never the number).</summary>
    public byte[]? AccountNumberHash { get; init; }

    public string Currency { get; init; } = "INR";

    public DateOnly? PeriodFrom { get; init; }

    public DateOnly? PeriodTo { get; init; }

    public decimal? OpeningBalance { get; init; }

    public decimal? ClosingBalance { get; init; }

    public int PageCount { get; init; }

    public TextSource TextSource { get; init; }

    public decimal? MeanConfidence { get; init; }

    public IReadOnlyList<BankTransaction> Transactions { get; init; } = [];
}

public sealed record PanData : ExtractedData
{
    /// <summary>Normalised PAN (upper case, no separators). Stored encrypted only; responses carry the masked form.</summary>
    public string? PanNumber { get; init; }

    public string? HolderTypeCode { get; init; }

    public string? NameOnCard { get; init; }

    public string? FatherName { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    /// <summary>True when an OCR confusion repair changed the number (a warning check accompanies it).</summary>
    public bool NumberOcrCorrected { get; init; }
}

public enum MrzCheckState
{
    NotPresent = 0,
    Valid,
    Invalid,
}

public sealed record MrzCheckStates(
    MrzCheckState DocumentNumber, MrzCheckState DateOfBirth, MrzCheckState DateOfExpiry, MrzCheckState PersonalNumber, MrzCheckState Composite);

public sealed record PassportData : ExtractedData
{
    public string? PassportNumber { get; init; }

    public string? IssuingCountry { get; init; }

    public string? Nationality { get; init; }

    public string? Surname { get; init; }

    public string? GivenNames { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public string? Sex { get; init; }

    public DateOnly? DateOfIssue { get; init; }

    public DateOnly? DateOfExpiry { get; init; }

    public bool MrzPresent { get; init; }

    /// <summary>Release 1 supports <c>TD3</c> only.</summary>
    public string? MrzFormat { get; init; }

    public MrzCheckStates MrzChecks { get; init; } = new(MrzCheckState.NotPresent, MrzCheckState.NotPresent, MrzCheckState.NotPresent, MrzCheckState.NotPresent, MrzCheckState.NotPresent);
}
