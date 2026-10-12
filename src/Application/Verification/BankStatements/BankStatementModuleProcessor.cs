using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.BankStatements;

/// <summary>
/// Bank statement pipeline: text (digital or OCR) -> rule set -> normalised statement -> rule and consistency checks -> comparison with the
/// submitted fields. There is no authoritative provider for statements in release 1, so the external stage is always NotRequested.
/// Results are review signals about internal consistency; nothing here says a statement is genuine.
/// </summary>
public sealed class BankStatementModuleProcessor : IModuleProcessor
{
    private readonly IDocumentParser _parser;
    private readonly IBankRuleSetProvider _ruleSets;
    private readonly IBankStatementParser _statementParser;
    private readonly INameMatcher _names;
    private readonly VerificationOptions _options;
    private readonly ILogger<BankStatementModuleProcessor> _logger;
    private readonly IPlatformCrypto? _crypto;

    public BankStatementModuleProcessor(
        IDocumentParser parser, IBankRuleSetProvider ruleSets, IBankStatementParser statementParser, INameMatcher names,
        IOptions<VerificationOptions> options, ILogger<BankStatementModuleProcessor> logger, IPlatformCrypto? crypto = null)
    {
        _parser = parser;
        _ruleSets = ruleSets;
        _statementParser = statementParser;
        _names = names;
        _options = options.Value;
        _logger = logger;
        _crypto = crypto;
    }

    public VerificationType Type => VerificationType.BankStatement;

    public async Task<DocumentStageOutcome> ProcessDocumentAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var parsed = await _parser.ParseAsync(context.Document, context.Kind, ProcessorSupport.ParseOptionsFor(_options, Type), cancellationToken).ConfigureAwait(false);
        if (parsed.IsFailure)
        {
            _logger.LogInformation("Bank statement text acquisition failed ({Code}) for {RequestId}", parsed.Error!.Code, context.Request.Id);
            return ProcessorSupport.Failed(ProcessorSupport.FailureCodeFor(parsed.Error));
        }

        var text = parsed.Value;
        var rules = _ruleSets.Detect(text);
        var result = _statementParser.Parse(text, rules);
        var data = result.Data;

        if (data.Transactions.Count == 0 && data.AccountHolderName is null && data.OpeningBalance is null && data.ClosingBalance is null)
        {
            _logger.LogInformation("Bank statement yielded no recognisable content for {RequestId} (rule set {RuleSet})", context.Request.Id, rules.Id);
            return ProcessorSupport.Failed(VerificationRules.FailureCodes.DocumentUnreadable);
        }

        if (_crypto is not null && BankStatementParser.FindAccountNumber(text, rules) is { } account)
        {
            var digits = new string(account.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (digits.Any(char.IsDigit))
            {
                data = data with { AccountNumberHash = _crypto.ComputeMac("verification-subject", Encoding.UTF8.GetBytes(digits)) };
            }
        }

        var checks = new List<CheckResult>(result.Checks);
        checks.AddRange(SubmittedChecks(context.SubmittedFields, data, text, rules));
        checks.Add(BankStatementChecks.ExternalNotPerformed());

        _logger.LogInformation(
            "Bank statement processed for {RequestId}: rule set {RuleSet}, {Rows} rows, {Failed} failed checks",
            context.Request.Id, rules.Id, data.Transactions.Count, checks.Count(c => c.Outcome == CheckOutcome.Fail));

        return new DocumentStageOutcome(
            StageState.Completed, StageState.Completed, StageState.Completed, data, checks, ProcessorSupport.ReasonsFrom(checks), null);
    }

    public Task<ExternalStageOutcome> VerifyExternalAsync(ModuleContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalStageOutcome(ExternalState.NotRequested, null, null, [], null));

    private IEnumerable<CheckResult> SubmittedChecks(IReadOnlyDictionary<string, string?> f, BankStatementData data, DocumentText text, BankRuleSet rules)
    {
        var holder = _names.Compare(SubmittedFieldKeys.Get(f, SubmittedFieldKeys.AccountHolderName), data.AccountHolderName).Outcome;
        var bank = _names.Compare(SubmittedFieldKeys.Get(f, SubmittedFieldKeys.BankName), data.BankName).Outcome;

        var last4 = SubmittedFieldKeys.Get(f, SubmittedFieldKeys.AccountNumberLast4);
        var extractedAccount = BankStatementParser.FindAccountNumber(text, rules);
        var extractedDigits = extractedAccount is null ? null : new string(extractedAccount.Where(char.IsDigit).ToArray());
        var accountOutcome = last4 is null ? FieldMatchOutcome.NotProvided
            : extractedDigits is not { Length: >= 4 } ? FieldMatchOutcome.NotExtracted
            : extractedDigits.EndsWith(last4, StringComparison.Ordinal) ? FieldMatchOutcome.Match : FieldMatchOutcome.Mismatch;

        var from = SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.PeriodFrom);
        var to = SubmittedFieldKeys.GetDate(f, SubmittedFieldKeys.PeriodTo);
        FieldMatchOutcome period;
        if (from is null && to is null)
        {
            period = FieldMatchOutcome.NotProvided;
        }
        else if (data.PeriodFrom is null || data.PeriodTo is null)
        {
            period = FieldMatchOutcome.NotExtracted;
        }
        else
        {
            var hits = (from is null || from == data.PeriodFrom ? 1 : 0) + (to is null || to == data.PeriodTo ? 1 : 0);
            period = hits == 2 ? FieldMatchOutcome.Match : hits == 1 ? FieldMatchOutcome.PartialMatch : FieldMatchOutcome.Mismatch;
        }

        var parts = new[] { holder, bank, accountOutcome, period };
        yield return ProcessorSupport.FieldCheck("BS_MATCH_HOLDER_NAME", "accountHolderName", holder);
        yield return ProcessorSupport.FieldCheck("BS_MATCH_BANK_NAME", "bankName", bank);
        yield return ProcessorSupport.FieldCheck("BS_MATCH_ACCOUNT_LAST4", "accountNumberLast4", accountOutcome);
        yield return ProcessorSupport.FieldCheck("BS_MATCH_PERIOD", "period", period);

        var evaluated = parts.Where(p => p is not (FieldMatchOutcome.NotProvided or FieldMatchOutcome.NotExtracted)).ToList();
        var aggregate = evaluated.Count == 0 ? FieldMatchOutcome.NotProvided
            : evaluated.Any(p => p == FieldMatchOutcome.Mismatch) ? FieldMatchOutcome.Mismatch
            : evaluated.All(p => p == FieldMatchOutcome.Match) ? FieldMatchOutcome.Match : FieldMatchOutcome.PartialMatch;
        yield return ProcessorSupport.FieldCheck(BankCheckCodes.SubmittedMatch, "submitted", aggregate);
    }
}
