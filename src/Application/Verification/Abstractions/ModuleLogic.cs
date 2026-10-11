using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Abstractions;

// ---- Bank statements -----------------------------------------------------------------------------------------------------------

public sealed record BankHeaderPatterns(
    string? HolderName, string? BankName, string? AccountNumber, string? PeriodFrom, string? PeriodTo, string? OpeningBalance, string? ClosingBalance);

/// <summary>Regex with named groups <c>date, valueDate, desc, ref, debit, credit, balance</c>. Rule files are deploy-time config, validated at start-up.</summary>
public sealed record BankTransactionPatterns(IReadOnlyList<string> Lines);

public sealed record AmountFormat(string DecimalSeparator, string ThousandsSeparator, bool LakhGrouping, bool DrCrSuffix);

public sealed record BankRuleSet(
    string Id, string DisplayName, IReadOnlyList<string> DetectPatterns, BankHeaderPatterns Header, BankTransactionPatterns Transactions,
    IReadOnlyList<string> DateFormats, AmountFormat Amounts, string DefaultCurrency);

public sealed record BankStatementParseResult(BankStatementData Data, IReadOnlyList<CheckResult> Checks);

public interface IBankStatementParser
{
    /// <summary>For example <c>generic-columnar</c>.</summary>
    string ParserId { get; }

    /// <summary>Pure and deterministic; regexes run with a match timeout.</summary>
    BankStatementParseResult Parse(DocumentText text, BankRuleSet rules);
}

public interface IBankRuleSetProvider
{
    /// <summary>Loaded from the configured JSON files; <c>generic</c> is always present.</summary>
    IReadOnlyList<BankRuleSet> RuleSets { get; }

    /// <summary>Best match by detection patterns; falls back to <c>generic</c>.</summary>
    BankRuleSet Detect(DocumentText text);
}

// ---- Authoritative providers (PAN, passport) -----------------------------------------------------------------------------------

/// <summary>Result of an authoritative check. <c>Verified</c> requires a provider reference (Rule V-1).</summary>
public sealed record ProviderResult(ExternalState State, string? ProviderReference, IReadOnlyList<FieldMatch> FieldMatches, string? ReasonCode);

public sealed record PanProviderRequest(string Pan, string? FullName, DateOnly? DateOfBirth, string CorrelationId)
{
    public override string ToString() => nameof(PanProviderRequest);
}

public sealed record PassportProviderRequest(
    string PassportNumber, string IssuingCountry, string? FullName, DateOnly? DateOfBirth, DateOnly? DateOfExpiry, string CorrelationId)
{
    public override string ToString() => nameof(PassportProviderRequest);
}

public interface IPanVerificationProvider
{
    string ProviderId { get; }

    bool IsConfigured { get; }

    /// <summary>Maps every expected failure to Timeout/ProviderError; never throws for them.</summary>
    Task<ProviderResult> VerifyAsync(PanProviderRequest request, CancellationToken cancellationToken);
}

public interface IPassportVerificationProvider
{
    string ProviderId { get; }

    bool IsConfigured { get; }

    Task<ProviderResult> VerifyAsync(PassportProviderRequest request, CancellationToken cancellationToken);
}

/// <summary>Selects the provider named by <c>Verification:Providers:Pan:Provider</c>. Only the DI registration knows vendor ids.</summary>
public interface IPanVerificationProviderResolver
{
    IPanVerificationProvider Resolve();
}

public interface IPassportVerificationProviderResolver
{
    IPassportVerificationProvider Resolve();
}
