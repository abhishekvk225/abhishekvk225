using FluentValidation;
using NexaVerify.Contracts.Api;

namespace NexaVerify.Application.Api;

public sealed class CreateApiKeyRequestValidator : AbstractValidator<CreateApiKeyRequest>
{
    public CreateApiKeyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Scopes).NotEmpty().Must(s => s.Count <= 20).WithMessage("At most 20 scopes.");
        RuleForEach(x => x.Scopes).NotEmpty().MaximumLength(60);
        RuleFor(x => x.RateLimitPerMinute).InclusiveBetween(1, 100_000).When(x => x.RateLimitPerMinute.HasValue);
        RuleFor(x => x.AllowedIps).Must(l => l!.Count <= 50).When(x => x.AllowedIps is not null).WithMessage("At most 50 entries.");
        RuleForEach(x => x.AllowedIps).Must(IpRules.IsValid).WithMessage("'{PropertyValue}' is not a valid IP address or CIDR range.");
    }
}

public sealed class UpdateApiKeyRequestValidator : AbstractValidator<UpdateApiKeyRequest>
{
    public UpdateApiKeyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Scopes).NotEmpty().Must(s => s.Count <= 20).WithMessage("At most 20 scopes.");
        RuleForEach(x => x.Scopes).NotEmpty().MaximumLength(60);
        RuleFor(x => x.RateLimitPerMinute).InclusiveBetween(1, 100_000).When(x => x.RateLimitPerMinute.HasValue);
        RuleFor(x => x.AllowedIps).Must(l => l!.Count <= 50).When(x => x.AllowedIps is not null).WithMessage("At most 50 entries.");
        RuleForEach(x => x.AllowedIps).Must(IpRules.IsValid).WithMessage("'{PropertyValue}' is not a valid IP address or CIDR range.");
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(64);
    }
}

public sealed class RevokeApiKeyRequestValidator : AbstractValidator<RevokeApiKeyRequest>
{
    public RevokeApiKeyRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class RegenerateApiKeyRequestValidator : AbstractValidator<RegenerateApiKeyRequest>
{
    public RegenerateApiKeyRequestValidator()
    {
        RuleFor(x => x.GraceMinutes).InclusiveBetween(0, 7 * 24 * 60);
    }
}

public sealed class EmergencyRevokeRequestValidator : AbstractValidator<EmergencyRevokeRequest>
{
    public EmergencyRevokeRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class SetApiAccessRequestValidator : AbstractValidator<SetApiAccessRequest>
{
    public SetApiAccessRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
