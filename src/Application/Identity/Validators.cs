using FluentValidation;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Application.Identity;

internal static class ValidationRules
{
    public static IRuleBuilderOptions<T, string?> ValidEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Email is required.")
            .MaximumLength(256).WithMessage("Email is too long.")
            .EmailAddress().WithMessage("Email is not valid.");

    public static IRuleBuilderOptionsConditions<T, string?> StrongPassword<T>(this IRuleBuilder<T, string?> rule, PasswordPolicy policy, Func<T, string?>? email = null) =>
        rule.Custom((value, context) =>
        {
            foreach (var problem in policy.Validate(value, email?.Invoke(context.InstanceToValidate)))
            {
                context.AddFailure(problem);
            }
        });
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required.").MaximumLength(1024);
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
    }
}

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(x => x.RefreshToken).MaximumLength(256);
    }
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator(PasswordPolicy policy)
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Token).NotEmpty().MaximumLength(256);
        RuleFor(x => x.NewPassword).StrongPassword(policy, x => x.Email);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator(PasswordPolicy policy)
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(1024);
        RuleFor(x => x.NewPassword).StrongPassword(policy);
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("The new password must differ from the current one.");
    }
}

public sealed class CreateRoleRequestValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60).Matches(@"^[A-Za-z][A-Za-z0-9 _-]*\z").WithMessage("Role names use letters, digits, spaces, - and _.");
        RuleFor(x => x.Scope).Must(s => s is "Platform" or "Client").WithMessage("Scope must be Platform or Client.");
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Permissions).NotNull().Must(p => p is { Count: <= 200 }).WithMessage("Too many permissions.");
        RuleForEach(x => x.Permissions).NotEmpty().MaximumLength(80);
    }
}

public sealed class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Permissions).NotNull().Must(p => p is { Count: <= 200 }).WithMessage("Too many permissions.");
        RuleForEach(x => x.Permissions).NotEmpty().MaximumLength(80);
    }
}

public sealed class CreatePlatformUserRequestValidator : AbstractValidator<CreatePlatformUserRequest>
{
    public CreatePlatformUserRequestValidator(PasswordPolicy policy)
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.TemporaryPassword).StrongPassword(policy, x => x.Email);
        RuleFor(x => x.Roles).NotNull().Must(r => r is { Count: > 0 and <= 20 }).WithMessage("Assign between 1 and 20 roles.");
        RuleForEach(x => x.Roles).NotEmpty().MaximumLength(60);
    }
}

public sealed class UpdatePlatformUserRequestValidator : AbstractValidator<UpdatePlatformUserRequest>
{
    public UpdatePlatformUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Roles).NotNull().Must(r => r is { Count: > 0 and <= 20 }).WithMessage("Assign between 1 and 20 roles.");
        RuleForEach(x => x.Roles).NotEmpty().MaximumLength(60);
    }
}

public sealed class PageRequestValidator : AbstractValidator<NexaVerify.Contracts.Common.PageRequest>
{
    public PageRequestValidator()
    {
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Sort).MaximumLength(50).Matches("^[A-Za-z]+(:(asc|desc))?$").When(x => !string.IsNullOrEmpty(x.Sort));
    }
}
