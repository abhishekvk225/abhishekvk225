using FluentValidation;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Application.Identity;

public sealed class ConfirmMfaRequestValidator : AbstractValidator<ConfirmMfaRequest>
{
    public ConfirmMfaRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Length(6, 8).WithMessage("Enter the 6-digit code from your authenticator app.");
    }
}

public sealed class RegenerateRecoveryCodesRequestValidator : AbstractValidator<RegenerateRecoveryCodesRequest>
{
    public RegenerateRecoveryCodesRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Length(6, 8).WithMessage("Enter the 6-digit code from your authenticator app.");
    }
}

public sealed class VerifyMfaRequestValidator : AbstractValidator<VerifyMfaRequest>
{
    public VerifyMfaRequestValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32);
    }
}

public sealed class ResetMfaRequestValidator : AbstractValidator<ResetMfaRequest>
{
    public ResetMfaRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A reason is required.").MaximumLength(500);
    }
}
