using FluentValidation;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Application.Tenancy;

internal static class TenancyRules
{
    public static IRuleBuilderOptions<T, string?> ClientName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Name is required.").MaximumLength(150);

    public static IRuleBuilderOptions<T, string?> Email<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Email is required.").MaximumLength(256).EmailAddress().WithMessage("Email is not valid.");

    /// <summary>IANA zone id shape (e.g. Europe/London, America/Argentina/Buenos_Aires, UTC). Existence is not checked: tzdata may be absent in containers.</summary>
    public static IRuleBuilderOptions<T, string?> TimeZoneId<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(64).Matches("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$").WithMessage("Time zone must be an IANA id such as Europe/London.");

    public static IRuleBuilderOptions<T, string?> OptionalText<T>(this IRuleBuilder<T, string?> rule, int max) =>
        rule.MaximumLength(max);

    public static IRuleBuilderOptions<T, string?> OptionalWebsite<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(200).Must(w => string.IsNullOrWhiteSpace(w)
            || (Uri.TryCreate(w, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)).WithMessage("Website must be an https URL.");

    public static IRuleBuilderOptions<T, string?> OptionalCountry<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(c => string.IsNullOrWhiteSpace(c) || (c.Trim().Length == 2 && c.Trim().All(char.IsAsciiLetter))).WithMessage("Country must be a 2-letter ISO code.");
}

public sealed class CreateClientRequestValidator : AbstractValidator<CreateClientRequest>
{
    public CreateClientRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30).Matches("^[A-Za-z0-9-]+$").WithMessage("Code may contain letters, digits and hyphens.");
        RuleFor(x => x.Name).ClientName();
        RuleFor(x => x.LegalName).OptionalText(200);
        RuleFor(x => x.ContactEmail).Email();
        RuleFor(x => x.ContactPhone).OptionalText(40);
        RuleFor(x => x.AddressLine1).OptionalText(150);
        RuleFor(x => x.AddressLine2).OptionalText(150);
        RuleFor(x => x.City).OptionalText(100);
        RuleFor(x => x.State).OptionalText(100);
        RuleFor(x => x.PostalCode).OptionalText(20);
        RuleFor(x => x.Country).OptionalCountry();
        RuleFor(x => x.Website).OptionalWebsite();
        RuleFor(x => x.Industry).OptionalText(100);
        RuleFor(x => x.TimeZone).TimeZoneId();
        RuleFor(x => x.Notes).OptionalText(1000);
        RuleFor(x => x.AdminEmail).Email();
        RuleFor(x => x.AdminFullName).NotEmpty().MaximumLength(150);
    }
}

public sealed class UpdateClientRequestValidator : AbstractValidator<UpdateClientRequest>
{
    public UpdateClientRequestValidator()
    {
        RuleFor(x => x.Name).ClientName();
        RuleFor(x => x.LegalName).OptionalText(200);
        RuleFor(x => x.ContactEmail).Email();
        RuleFor(x => x.ContactPhone).OptionalText(40);
        RuleFor(x => x.AddressLine1).OptionalText(150);
        RuleFor(x => x.AddressLine2).OptionalText(150);
        RuleFor(x => x.City).OptionalText(100);
        RuleFor(x => x.State).OptionalText(100);
        RuleFor(x => x.PostalCode).OptionalText(20);
        RuleFor(x => x.Country).OptionalCountry();
        RuleFor(x => x.Website).OptionalWebsite();
        RuleFor(x => x.Industry).OptionalText(100);
        RuleFor(x => x.TimeZone).TimeZoneId();
        RuleFor(x => x.Notes).OptionalText(1000);
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(64);
    }
}

public sealed class UpdateClientProfileRequestValidator : AbstractValidator<UpdateClientProfileRequest>
{
    public UpdateClientProfileRequestValidator()
    {
        RuleFor(x => x.Name).ClientName();
        RuleFor(x => x.LegalName).OptionalText(200);
        RuleFor(x => x.ContactEmail).Email();
        RuleFor(x => x.ContactPhone).OptionalText(40);
        RuleFor(x => x.AddressLine1).OptionalText(150);
        RuleFor(x => x.AddressLine2).OptionalText(150);
        RuleFor(x => x.City).OptionalText(100);
        RuleFor(x => x.State).OptionalText(100);
        RuleFor(x => x.PostalCode).OptionalText(20);
        RuleFor(x => x.Country).OptionalCountry();
        RuleFor(x => x.Website).OptionalWebsite();
        RuleFor(x => x.Industry).OptionalText(100);
        RuleFor(x => x.TimeZone).TimeZoneId();
        RuleFor(x => x.RowVersion).NotEmpty().MaximumLength(64);
    }
}

public sealed class ClientStatusRequestValidator : AbstractValidator<ClientStatusRequest>
{
    public ClientStatusRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class CreateClientUserRequestValidator : AbstractValidator<CreateClientUserRequest>
{
    public CreateClientUserRequestValidator()
    {
        RuleFor(x => x.Email).Email();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Role).NotEmpty().MaximumLength(60);
        RuleFor(x => x.JobTitle).OptionalText(100);
    }
}

public sealed class UpdateClientUserRequestValidator : AbstractValidator<UpdateClientUserRequest>
{
    public UpdateClientUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Role).NotEmpty().MaximumLength(60);
        RuleFor(x => x.JobTitle).OptionalText(100);
    }
}

public sealed class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(x => x.Values).NotNull().Must(v => v is { Count: > 0 and <= 50 }).WithMessage("Send between 1 and 50 settings.");
    }
}
