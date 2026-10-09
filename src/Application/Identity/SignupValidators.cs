using FluentValidation;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Public;

namespace NexaVerify.Application.Identity;

internal static class PublicValidationRules
{
    /// <summary>A plausible address once surrounding whitespace is removed (forms and pasted text often carry some).</summary>
    public static IRuleBuilderOptions<T, string?> TrimmedEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Email is required.")
            .MaximumLength(256).WithMessage("Email is too long.")
            .Must(e => e is not null && System.Net.Mail.MailAddress.TryCreate(e.Trim(), out var parsed) && parsed.Address == e.Trim() && parsed.Host.Contains('.'))
            .WithMessage("Email is not valid.");
}

public sealed class SignupRequestValidator : AbstractValidator<SignupRequest>
{
    public SignupRequestValidator(PasswordPolicy policy, DisposableEmailPolicy disposable)
    {
        RuleFor(x => x.CompanyName).NotEmpty().WithMessage("Company name is required.").MaximumLength(150);
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Your name is required.").MaximumLength(150);
        RuleFor(x => x.Email).TrimmedEmail()
            .Must(e => !disposable.IsDisposable(e)).WithMessage("Please use your work email address.");
        RuleFor(x => x.Password).StrongPassword(policy, x => x.Email);
        RuleFor(x => x.AcceptTerms).Equal(true).WithMessage("You must accept the terms to create an account.");
        RuleFor(x => x.CaptchaToken).MaximumLength(4096);
    }
}

public sealed class ResendSignupRequestValidator : AbstractValidator<ResendSignupRequest>
{
    public ResendSignupRequestValidator()
    {
        RuleFor(x => x.Email).TrimmedEmail();
    }
}

public sealed class VerifySignupRequestValidator : AbstractValidator<VerifySignupRequest>
{
    public VerifySignupRequestValidator()
    {
        RuleFor(x => x.Email).TrimmedEmail();
        RuleFor(x => x.Token).NotEmpty().MaximumLength(256);
    }
}

public sealed class SubmitContactRequestValidator : AbstractValidator<SubmitContactRequest>
{
    public SubmitContactRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Your name is required.").MaximumLength(150);
        RuleFor(x => x.Email).TrimmedEmail();
        RuleFor(x => x.Company).MaximumLength(150);
        RuleFor(x => x.Message).NotEmpty().WithMessage("A message is required.").MaximumLength(4000);
    }
}
