using FluentValidation;
using NexaVerify.Contracts.Licensing;

namespace NexaVerify.Application.Licensing;

public sealed class CreateLicenseRequestValidator : AbstractValidator<CreateLicenseRequest>
{
    public CreateLicenseRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.TotalCredits).InclusiveBetween(0, 100_000_000).When(x => x.TotalCredits.HasValue);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.ExpiresAt).GreaterThan(x => x.StartsAt!.Value).When(x => x.StartsAt.HasValue && x.ExpiresAt.HasValue);
    }
}

public sealed class UpdateLicenseRequestValidator : AbstractValidator<UpdateLicenseRequest>
{
    public UpdateLicenseRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(64);
    }
}

public sealed class LicenseReasonRequestValidator : AbstractValidator<LicenseReasonRequest>
{
    public LicenseReasonRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class RenewLicenseRequestValidator : AbstractValidator<RenewLicenseRequest>
{
    public RenewLicenseRequestValidator()
    {
        RuleFor(x => x.AdditionalCredits).InclusiveBetween(0, 100_000_000);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class AdjustLicenseRequestValidator : AbstractValidator<AdjustLicenseRequest>
{
    public AdjustLicenseRequestValidator()
    {
        RuleFor(x => x.Credits).NotEqual(0).InclusiveBetween(-100_000_000, 100_000_000);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class ApproveAdjustmentRequestValidator : AbstractValidator<ApproveAdjustmentRequest>
{
    public ApproveAdjustmentRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class RejectAdjustmentRequestValidator : AbstractValidator<RejectAdjustmentRequest>
{
    public RejectAdjustmentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class VerifyLedgerRequestValidator : AbstractValidator<VerifyLedgerRequest>
{
    public VerifyLedgerRequestValidator()
    {
        RuleFor(x => x.LicenseId).NotEqual(Guid.Empty).When(x => x.LicenseId.HasValue);
    }
}

public sealed class RefundRequestValidator : AbstractValidator<RefundRequest>
{
    public RefundRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class SavePlanRequestValidator : AbstractValidator<SavePlanRequest>
{
    public SavePlanRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9-]+$");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.DefaultCredits).InclusiveBetween(0, 100_000_000);
        RuleFor(x => x.DefaultDurationDays).InclusiveBetween(1, 3650);
        RuleFor(x => x.RateLimitPerMinute).InclusiveBetween(1, 100_000);
        RuleFor(x => x.DailyQuota).GreaterThan(0).When(x => x.DailyQuota.HasValue);
        RuleFor(x => x.MaxFaceProfiles).GreaterThan(0).When(x => x.MaxFaceProfiles.HasValue);
        RuleFor(x => x.MaxApiKeys).InclusiveBetween(0, 1000);
        RuleFor(x => x.MaxUsers).InclusiveBetween(1, 100_000);
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 10_000).When(x => x.DisplayOrder.HasValue);
        RuleFor(x => x.DisplayPrice).MaximumLength(60);
        RuleFor(x => x.Highlights).Must(h => h is null || h.Count <= 10).WithMessage("At most 10 highlights.");
        RuleForEach(x => x.Highlights).NotEmpty().MaximumLength(120);
    }
}

public sealed class SetCostRuleRequestValidator : AbstractValidator<SetCostRuleRequest>
{
    public SetCostRuleRequestValidator()
    {
        RuleFor(x => x.Operation).NotEmpty().MaximumLength(30);
        RuleFor(x => x.ChargePolicy).NotEmpty().MaximumLength(30);
        RuleFor(x => x.Credits).InclusiveBetween(0, 1000);
    }
}
