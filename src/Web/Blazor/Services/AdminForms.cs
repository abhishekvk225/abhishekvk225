using System.ComponentModel.DataAnnotations;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Web.Services;

/// <summary>Optional https URL, mirroring the API rule (the API stays authoritative).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HttpsUrlAttribute : ValidationAttribute
{
    public HttpsUrlAttribute()
        : base("Website must be an https address, e.g. https://example.com.")
    {
    }

    public override bool IsValid(object? value) =>
        value is not string text || string.IsNullOrWhiteSpace(text)
        || (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>Form model for creating and editing a client. Field rules mirror the API validators so users see problems inline.</summary>
public sealed class ClientForm
{
    [Required(ErrorMessage = "Enter a short code for the client."), StringLength(30)]
    [RegularExpression("^[A-Za-z0-9-]+$", ErrorMessage = "Use letters, digits and hyphens only.")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the company name."), StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(200)]
    public string? LegalName { get; set; }

    [Required(ErrorMessage = "Enter a contact email."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256)]
    public string ContactEmail { get; set; } = string.Empty;

    [StringLength(40)]
    public string? ContactPhone { get; set; }

    [StringLength(150)]
    public string? AddressLine1 { get; set; }

    [StringLength(150)]
    public string? AddressLine2 { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(100)]
    public string? State { get; set; }

    [StringLength(20)]
    public string? PostalCode { get; set; }

    [RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "Use a 2-letter country code, e.g. GB.")]
    public string? Country { get; set; }

    [StringLength(200), HttpsUrl]
    public string? Website { get; set; }

    [StringLength(100)]
    public string? Industry { get; set; }

    [Required(ErrorMessage = "Choose a time zone."), StringLength(64)]
    [RegularExpression("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$", ErrorMessage = "Use a time zone id such as Europe/London.")]
    public string TimeZone { get; set; } = "UTC";

    [StringLength(1000)]
    public string? Notes { get; set; }

    [Required(ErrorMessage = "Enter the administrator's email."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256)]
    public string AdminEmail { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the administrator's name."), StringLength(150)]
    public string AdminFullName { get; set; } = string.Empty;

    /// <summary>Wizard step 3 (optional): a plan to issue a first license from.</summary>
    public Guid? InitialPlanId { get; set; }

    public int? InitialCredits { get; set; }

    public string RowVersion { get; set; } = string.Empty;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public CreateClientRequest ToCreateRequest() => new(
        Code.Trim(), Name.Trim(), Clean(LegalName), ContactEmail.Trim(), Clean(ContactPhone),
        Clean(AddressLine1), Clean(AddressLine2), Clean(City), Clean(State), Clean(PostalCode), Clean(Country)?.ToUpperInvariant(),
        Clean(Website), Clean(Industry), TimeZone.Trim(), Clean(Notes), AdminEmail.Trim(), AdminFullName.Trim());

    public UpdateClientRequest ToUpdateRequest() => new(
        Name.Trim(), Clean(LegalName), ContactEmail.Trim(), Clean(ContactPhone),
        Clean(AddressLine1), Clean(AddressLine2), Clean(City), Clean(State), Clean(PostalCode), Clean(Country)?.ToUpperInvariant(),
        Clean(Website), Clean(Industry), TimeZone.Trim(), Clean(Notes), RowVersion);

    public static ClientForm From(ClientDto c) => new()
    {
        Code = c.Code, Name = c.Name, LegalName = c.LegalName, ContactEmail = c.ContactEmail, ContactPhone = c.ContactPhone,
        AddressLine1 = c.AddressLine1, AddressLine2 = c.AddressLine2, City = c.City, State = c.State, PostalCode = c.PostalCode,
        Country = c.Country, Website = c.Website, Industry = c.Industry, TimeZone = c.TimeZone, Notes = c.Notes, RowVersion = c.RowVersion,
    };
}

public sealed class IssueLicenseForm
{
    public Guid? ClientId { get; set; }

    public Guid? PlanId { get; set; }

    [Required(ErrorMessage = "Give the license a name."), StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [Range(0, 100_000_000, ErrorMessage = "Credits must be between 0 and 100,000,000.")]
    public int? TotalCredits { get; set; }

    public DateTime? StartsAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public CreateLicenseRequest ToRequest() => new(
        PlanId, Name.Trim(), TotalCredits, AsUtc(StartsAt), AsUtc(ExpiresAt), string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());

    internal static DateTime? AsUtc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
}

public sealed class RenewLicenseForm
{
    [Required(ErrorMessage = "Choose the new expiry date.")]
    public DateTime? ExpiresAt { get; set; }

    [Range(0, 100_000_000, ErrorMessage = "Credits must be between 0 and 100,000,000.")]
    public int AdditionalCredits { get; set; }

    [StringLength(500)]
    public string? Reason { get; set; }

    public RenewLicenseRequest ToRequest() =>
        new(IssueLicenseForm.AsUtc(ExpiresAt)!.Value, AdditionalCredits, string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim());
}

public sealed class AdjustLicenseForm : IValidatableObject
{
    [Range(-100_000_000, 100_000_000, ErrorMessage = "Use a number between -100,000,000 and 100,000,000.")]
    public int Credits { get; set; }

    [Required(ErrorMessage = "Explain why the credits are changing (this is recorded)."), StringLength(500)]
    public string Reason { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Credits == 0)
        {
            yield return new ValidationResult("Enter a positive number to add credits or a negative number to remove them.", [nameof(Credits)]);
        }
    }

    public AdjustLicenseRequest ToRequest() => new(Credits, Reason.Trim());
}

public sealed class LicenseNameForm
{
    [Required(ErrorMessage = "Give the license a name."), StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Notes { get; set; }

    public string RowVersion { get; set; } = string.Empty;

    public UpdateLicenseRequest ToRequest() => new(Name.Trim(), string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(), RowVersion);
}

public sealed class RefundForm
{
    [Required(ErrorMessage = "Explain why this is refunded (this is recorded)."), StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class PlanForm
{
    [Required(ErrorMessage = "Enter a short code."), StringLength(30)]
    [RegularExpression("^[A-Za-z0-9-]+$", ErrorMessage = "Use letters, digits and hyphens only.")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the plan name."), StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Range(0, 100_000_000)]
    public int DefaultCredits { get; set; } = 1000;

    [Range(1, 3650, ErrorMessage = "Use between 1 and 3,650 days.")]
    public int DefaultDurationDays { get; set; } = 365;

    [Range(1, 100_000)]
    public int RateLimitPerMinute { get; set; } = 60;

    [Range(1, int.MaxValue, ErrorMessage = "Leave empty for no limit, or enter a number above 0.")]
    public int? DailyQuota { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Leave empty for no limit, or enter a number above 0.")]
    public int? MaxFaceProfiles { get; set; }

    [Range(0, 1000)]
    public int MaxApiKeys { get; set; } = 5;

    [Range(1, 100_000)]
    public int MaxUsers { get; set; } = 10;

    public bool IsActive { get; set; } = true;

    public SavePlanRequest ToRequest() => new(
        Code.Trim(), Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), DefaultCredits, DefaultDurationDays,
        RateLimitPerMinute, DailyQuota, MaxFaceProfiles, MaxApiKeys, MaxUsers, IsActive);

    public static PlanForm From(PlanDto p) => new()
    {
        Code = p.Code, Name = p.Name, Description = p.Description, DefaultCredits = p.DefaultCredits, DefaultDurationDays = p.DefaultDurationDays,
        RateLimitPerMinute = p.RateLimitPerMinute, DailyQuota = p.DailyQuota, MaxFaceProfiles = p.MaxFaceProfiles, MaxApiKeys = p.MaxApiKeys,
        MaxUsers = p.MaxUsers, IsActive = p.IsActive,
    };
}

/// <summary>Operations a cost rule can price, and when a charge applies (mirrors the API enums).</summary>
public static class CostRuleOptions
{
    public static readonly IReadOnlyList<string> Operations = ["Enroll", "Verify", "Identify", "Detect"];

    public static readonly IReadOnlyList<(string Value, string Label)> Policies =
    [
        ("OnCompleted", "When the check finishes (including no match)"),
        ("OnSuccess", "Only when it succeeds"),
        ("OnAttempt", "On every attempt, even failed ones"),
    ];

    public static string PolicyLabel(string value) => Policies.FirstOrDefault(p => p.Value == value).Label ?? value;
}

public sealed class CostRuleForm
{
    /// <summary>Null = the platform default; otherwise the plan the rule belongs to.</summary>
    public Guid? PlanId { get; set; }

    [Required(ErrorMessage = "Choose an operation.")]
    public string Operation { get; set; } = "Verify";

    [Range(0, 1000, ErrorMessage = "Use a number between 0 and 1,000.")]
    public int Credits { get; set; } = 1;

    [Required]
    public string ChargePolicy { get; set; } = "OnCompleted";

    public SetCostRuleRequest ToRequest() => new(Operation, Credits, ChargePolicy, null);
}

public sealed class PlatformUserForm : IValidatableObject
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the person's name."), StringLength(150)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Only used when creating. The person must change it at first sign-in.</summary>
    [StringLength(128)]
    public string TemporaryPassword { get; set; } = string.Empty;

    public IReadOnlyCollection<string> Roles { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Id is null && TemporaryPassword.Length < PasswordRules.MinLength)
        {
            yield return new ValidationResult($"Use at least {PasswordRules.MinLength} characters.", [nameof(TemporaryPassword)]);
        }

        if (Roles.Count == 0)
        {
            yield return new ValidationResult("Choose at least one role.", [nameof(Roles)]);
        }
    }

    public CreatePlatformUserRequest ToCreateRequest() => new(Email.Trim(), FullName.Trim(), TemporaryPassword, Roles.ToList());

    public UpdatePlatformUserRequest ToUpdateRequest() => new(FullName.Trim(), Roles.ToList(), IsActive);
}

public sealed class RoleForm
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "Enter a role name."), StringLength(60)]
    public string Name { get; set; } = string.Empty;

    [StringLength(300)]
    public string? Description { get; set; }

    public string Scope { get; set; } = "Platform";

    public IReadOnlyCollection<string> Permissions { get; set; } = [];

    public CreateRoleRequest ToCreateRequest() =>
        new(Name.Trim(), Scope, string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), Permissions.ToList());

    public UpdateRoleRequest ToUpdateRequest() =>
        new(string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), Permissions.ToList());
}
