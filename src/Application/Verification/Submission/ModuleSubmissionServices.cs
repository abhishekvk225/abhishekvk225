using System.Globalization;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Submission;

// One small service per module (REQ §3): it maps its own contract to the shared pipeline. Field keys are SubmittedFieldKeys
// (read by the module processors); dates travel as ISO yyyy-MM-dd.

internal static class FieldMap
{
    public static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class BankStatementVerificationService : IBankStatementVerificationService
{
    private readonly VerificationSubmissionCore _core;

    public BankStatementVerificationService(VerificationSubmissionCore core)
    {
        _core = core;
    }

    public VerificationType Type => VerificationType.BankStatement;

    public Task<Result<VerificationAcceptedDto>> SubmitAsync(SubmitBankStatementRequest request, DocumentUpload? document, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SubmittedFieldKeys.AccountHolderName] = FieldMap.Text(request.AccountHolderName),
            [SubmittedFieldKeys.BankName] = FieldMap.Text(request.BankName),
            [SubmittedFieldKeys.AccountNumberLast4] = FieldMap.Text(request.AccountNumberLast4),
            [SubmittedFieldKeys.PeriodFrom] = FieldMap.Date(request.PeriodFrom),
            [SubmittedFieldKeys.PeriodTo] = FieldMap.Date(request.PeriodTo),
        };
        return _core.SubmitAsync(new SubmissionPlan(Type, request.ExternalRef, request.ConsentReference, fields, SubmissionDocumentRule.Required), document, idempotencyKey, cancellationToken);
    }
}

public sealed class PanVerificationService : IPanVerificationService
{
    private readonly VerificationSubmissionCore _core;

    public PanVerificationService(VerificationSubmissionCore core)
    {
        _core = core;
    }

    public VerificationType Type => VerificationType.Pan;

    public Task<Result<VerificationAcceptedDto>> SubmitAsync(SubmitPanRequest request, DocumentUpload? document, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SubmittedFieldKeys.PanNumber] = FieldMap.Text(request.PanNumber),
            [SubmittedFieldKeys.FullName] = FieldMap.Text(request.FullName),
            [SubmittedFieldKeys.DateOfBirth] = FieldMap.Date(request.DateOfBirth),
            [SubmittedFieldKeys.FatherName] = FieldMap.Text(request.FatherName),
        };
        return _core.SubmitAsync(new SubmissionPlan(Type, request.ExternalRef, request.ConsentReference, fields, SubmissionDocumentRule.DocumentOrNumber), document, idempotencyKey, cancellationToken);
    }
}

public sealed class PassportVerificationService : IPassportVerificationService
{
    private readonly VerificationSubmissionCore _core;

    public PassportVerificationService(VerificationSubmissionCore core)
    {
        _core = core;
    }

    public VerificationType Type => VerificationType.Passport;

    public Task<Result<VerificationAcceptedDto>> SubmitAsync(SubmitPassportRequest request, DocumentUpload? document, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SubmittedFieldKeys.PassportNumber] = FieldMap.Text(request.PassportNumber),
            [SubmittedFieldKeys.FullName] = FieldMap.Text(request.FullName),
            [SubmittedFieldKeys.DateOfBirth] = FieldMap.Date(request.DateOfBirth),
            [SubmittedFieldKeys.Nationality] = FieldMap.Text(request.Nationality)?.ToUpperInvariant(),
            [SubmittedFieldKeys.DateOfIssue] = FieldMap.Date(request.DateOfIssue),
            [SubmittedFieldKeys.DateOfExpiry] = FieldMap.Date(request.DateOfExpiry),
        };
        return _core.SubmitAsync(new SubmissionPlan(Type, request.ExternalRef, request.ConsentReference, fields, SubmissionDocumentRule.Required), document, idempotencyKey, cancellationToken);
    }
}
