using System.Linq.Expressions;
using FluentValidation;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Application.Verification.Submission;

/// <summary>Shared field rules for the three submit requests.</summary>
internal static class SubmitRules
{
    public const int ExternalRefMax = 100;
    public const int ConsentMax = 100;
    public const int NameMax = 150;

    public static void Common<T>(
        AbstractValidator<T> v, Expression<Func<T, string?>> externalRef, Expression<Func<T, string?>> consent, bool requireConsent)
    {
        v.RuleFor(externalRef).MaximumLength(ExternalRefMax);
        var consentRule = v.RuleFor(consent).MaximumLength(ConsentMax);
        if (requireConsent)
        {
            consentRule.NotEmpty();
        }
    }
}

public sealed class SubmitBankStatementRequestValidator : AbstractValidator<SubmitBankStatementRequest>
{
    public SubmitBankStatementRequestValidator(IOptions<VerificationOptions> options)
    {
        SubmitRules.Common(this, x => x.ExternalRef, x => x.ConsentReference, options.Value.RequireConsentReference);
        RuleFor(x => x.AccountHolderName).MaximumLength(SubmitRules.NameMax);
        RuleFor(x => x.BankName).MaximumLength(100);
        RuleFor(x => x.AccountNumberLast4).Matches(@"^\d{4}$").When(x => x.AccountNumberLast4 is not null).WithMessage("Must be exactly four digits.");
        RuleFor(x => x).Must(x => x.PeriodFrom is null || x.PeriodTo is null || x.PeriodFrom <= x.PeriodTo)
            .WithName("periodFrom").WithMessage("periodFrom must not be after periodTo.");
    }
}

public sealed class SubmitPanRequestValidator : AbstractValidator<SubmitPanRequest>
{
    public SubmitPanRequestValidator(IOptions<VerificationOptions> options)
    {
        SubmitRules.Common(this, x => x.ExternalRef, x => x.ConsentReference, options.Value.RequireConsentReference);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(SubmitRules.NameMax);
        RuleFor(x => x.FatherName).MaximumLength(SubmitRules.NameMax);
        RuleFor(x => x.PanNumber).MaximumLength(20);
    }
}

public sealed class SubmitPassportRequestValidator : AbstractValidator<SubmitPassportRequest>
{
    public SubmitPassportRequestValidator(IOptions<VerificationOptions> options)
    {
        SubmitRules.Common(this, x => x.ExternalRef, x => x.ConsentReference, options.Value.RequireConsentReference);
        RuleFor(x => x.PassportNumber).MaximumLength(20);
        RuleFor(x => x.FullName).MaximumLength(SubmitRules.NameMax);
        RuleFor(x => x.Nationality).Matches("^[A-Za-z]{3}$").When(x => x.Nationality is not null).WithMessage("Must be an ISO 3166 alpha-3 code.");
        RuleFor(x => x).Must(x => x.DateOfIssue is null || x.DateOfExpiry is null || x.DateOfIssue < x.DateOfExpiry)
            .WithName("dateOfIssue").WithMessage("dateOfIssue must be before dateOfExpiry.");
    }
}

public sealed class UpdateVerificationModuleRequestValidator : AbstractValidator<UpdateVerificationModuleRequest>
{
    public UpdateVerificationModuleRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(100);
    }
}

public sealed class VerificationDownloadRequestValidator : AbstractValidator<VerificationDownloadRequest>
{
    public VerificationDownloadRequestValidator()
    {
        RuleFor(x => x.Kind).Must(k => k is VerificationDownloadKinds.Report or VerificationDownloadKinds.TransactionsCsv or VerificationDownloadKinds.OriginalDocument)
            .WithMessage("Kind must be Report, TransactionsCsv or OriginalDocument.");
        RuleFor(x => x.Format).Must(f => f is VerificationDownloadKinds.Json or VerificationDownloadKinds.Html)
            .When(x => x.Format is not null).WithMessage("Format must be json or html.");
    }
}
