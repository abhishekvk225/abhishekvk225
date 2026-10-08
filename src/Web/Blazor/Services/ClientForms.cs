using System.ComponentModel.DataAnnotations;
using System.Net;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Web.Services;

/// <summary>Limits the API enforces for photos. The portal checks them first only to give early, friendly feedback.</summary>
public static class FaceLimits
{
    public const long MaxImageBytes = 5 * 1024 * 1024;
}

/// <summary>Last-moment check before a photo leaves the portal (size, real image type). Returns a friendly message or null.</summary>
public static class PhotoGuard
{
    public static string? Check(NexaVerify.Web.Components.CapturedImage? image)
    {
        if (image is null || image.Data.Length == 0)
        {
            return "Add a photo first.";
        }

        if (image.Data.Length > FaceLimits.MaxImageBytes)
        {
            return "That photo is too large. The limit is 5 MB.";
        }

        return NexaVerify.Web.Components.ImageSniffer.Detect(image.Data) is null
            ? "That file does not look like a valid photo. Please use a JPEG or PNG photo."
            : null;
    }

    /// <summary>A fresh key for every submit, so the same click can never be charged twice.</summary>
    public static string NewIdempotencyKey() => Guid.NewGuid().ToString("N");

    /// <summary>Overwrites the photo bytes once the request is done. The portal never keeps images.</summary>
    public static void Forget(NexaVerify.Web.Components.CapturedImage? image)
    {
        if (image is not null)
        {
            Array.Clear(image.Data);
        }
    }
}

/// <summary>Enrolment details. Field rules mirror the API validator so users see problems inline; the API stays authoritative.</summary>
public sealed class EnrollForm
{
    [Required(ErrorMessage = "Enter your own reference for this person (for example an employee number)."), StringLength(100)]
    public string ExternalRef { get; set; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; set; }

    [Required(ErrorMessage = "Enter where the person's consent is recorded (for example a form number)."), StringLength(200)]
    public string ConsentReference { get; set; } = string.Empty;

    [StringLength(4096, ErrorMessage = "Extra details can be at most 4,096 characters.")]
    public string? Metadata { get; set; }

    public EnrollFaceRequest ToRequest() =>
        new(ExternalRef.Trim(), Blank(DisplayName), Blank(Metadata), ConsentReference.Trim());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class VerifyForm
{
    [Required(ErrorMessage = "Enter the reference of the person to check against."), StringLength(100)]
    public string ExternalRef { get; set; } = string.Empty;

    public VerifyFaceRequest ToRequest() => new(null, ExternalRef.Trim());
}

public sealed class IdentifyForm
{
    [Range(1, 20, ErrorMessage = "Choose between 1 and 20 candidates.")]
    public int? TopK { get; set; } = 5;

    public IdentifyFaceRequest ToRequest() => new(TopK);
}

/// <summary>Create / edit an API key.</summary>
public sealed class ApiKeyForm : IValidatableObject
{
    public Guid? Id { get; set; }

    public string? RowVersion { get; set; }

    [Required(ErrorMessage = "Give the key a name so you can recognise it later."), StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public HashSet<string> Scopes { get; set; } = new(StringComparer.Ordinal);

    public DateTime? ExpiresOn { get; set; }

    [Range(1, 100_000, ErrorMessage = "Enter a number between 1 and 100,000.")]
    public int? RateLimitPerMinute { get; set; }

    /// <summary>One address or range per line.</summary>
    public string? AllowedIpsText { get; set; }

    public IReadOnlyList<string> AllowedIps => (AllowedIpsText ?? string.Empty)
        .Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public static ApiKeyForm From(ApiKeyDto key) => new()
    {
        Id = key.Id,
        RowVersion = key.RowVersion,
        Name = key.Name,
        Scopes = new HashSet<string>(key.Scopes, StringComparer.Ordinal),
        ExpiresOn = key.ExpiresAt?.Date,
        RateLimitPerMinute = key.RateLimitPerMinute,
        AllowedIpsText = string.Join('\n', key.AllowedIps),
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Scopes.Count == 0)
        {
            yield return new ValidationResult("Choose at least one thing this key may do.", [nameof(Scopes)]);
        }

        if (AllowedIps.Count > 50)
        {
            yield return new ValidationResult("At most 50 addresses.", [nameof(AllowedIpsText)]);
        }

        foreach (var rule in AllowedIps.Where(r => !IsIpOrCidr(r)))
        {
            yield return new ValidationResult($"'{rule}' is not a valid IP address or range such as 203.0.113.0/24.", [nameof(AllowedIpsText)]);
        }
    }

    public CreateApiKeyRequest ToCreate() => new(Name.Trim(), Scopes.Order(StringComparer.Ordinal).ToList(), EndOfDayUtc(ExpiresOn), RateLimitPerMinute, AllowedIps);

    public UpdateApiKeyRequest ToUpdate() => new(Name.Trim(), Scopes.Order(StringComparer.Ordinal).ToList(), EndOfDayUtc(ExpiresOn), RateLimitPerMinute, AllowedIps, RowVersion ?? string.Empty);

    private static DateTime? EndOfDayUtc(DateTime? date) =>
        date is { } d ? DateTime.SpecifyKind(d.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc) : null;

    internal static bool IsIpOrCidr(string rule)
    {
        var parts = rule.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return true;
        }

        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bits) && bits >= 0 && bits <= max;
    }
}

public sealed class RegenerateKeyForm
{
    [Range(0, 7 * 24 * 60, ErrorMessage = "Choose between 0 minutes and 7 days (10,080 minutes).")]
    public int GraceMinutes { get; set; } = 60;
}

public sealed class RevokeKeyForm
{
    [Required(ErrorMessage = "Say why you are revoking this key."), StringLength(500)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Create / edit a webhook endpoint.</summary>
public sealed class WebhookForm
{
    public Guid? Id { get; set; }

    public string? RowVersion { get; set; }

    [Required(ErrorMessage = "Give the endpoint a name."), StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the address that should receive events."), StringLength(500)]
    [HttpsRequired]
    public string Url { get; set; } = string.Empty;

    [MinLength(1, ErrorMessage = "Choose at least one event.")]
    public HashSet<string> Events { get; set; } = new(StringComparer.Ordinal);

    public bool Enabled { get; set; } = true;

    public static WebhookForm From(WebhookEndpointDto endpoint) => new()
    {
        Id = endpoint.Id,
        RowVersion = endpoint.RowVersion,
        Name = endpoint.Name,
        Url = endpoint.Url,
        Events = new HashSet<string>(endpoint.Events, StringComparer.Ordinal),
        Enabled = string.Equals(endpoint.Status, "Active", StringComparison.OrdinalIgnoreCase),
    };

    public CreateWebhookRequest ToCreate() => new(Name.Trim(), Url.Trim(), Events.Order(StringComparer.Ordinal).ToList());

    public UpdateWebhookRequest ToUpdate() => new(Name.Trim(), Url.Trim(), Events.Order(StringComparer.Ordinal).ToList(), Enabled, RowVersion ?? string.Empty);
}

/// <summary>A required https address (the API additionally refuses private and local targets).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HttpsRequiredAttribute : ValidationAttribute
{
    public HttpsRequiredAttribute()
        : base("The address must start with https://, for example https://example.com/hooks.")
    {
    }

    public override bool IsValid(object? value) =>
        value is not string text || string.IsNullOrWhiteSpace(text)
        || (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
}

public sealed class InviteUserForm
{
    public Guid? Id { get; set; }

    [Required(ErrorMessage = "Enter the person's email address."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the person's name."), StringLength(150)]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a role."), StringLength(60)]
    public string Role { get; set; } = "ClientUser";

    [StringLength(100)]
    public string? JobTitle { get; set; }

    public bool IsActive { get; set; } = true;

    public static InviteUserForm From(ClientUserDto user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FullName = user.FullName,
        Role = user.Role,
        JobTitle = user.JobTitle,
        IsActive = string.Equals(user.Status, "Active", StringComparison.OrdinalIgnoreCase),
    };

    public CreateClientUserRequest ToCreate() => new(Email.Trim(), FullName.Trim(), Role, string.IsNullOrWhiteSpace(JobTitle) ? null : JobTitle.Trim());

    public UpdateClientUserRequest ToUpdate() => new(FullName.Trim(), Role, string.IsNullOrWhiteSpace(JobTitle) ? null : JobTitle.Trim(), IsActive);
}

/// <summary>The company details a client may edit about itself.</summary>
public sealed class CompanyProfileForm
{
    public string RowVersion { get; set; } = string.Empty;

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
    public string TimeZone { get; set; } = "UTC";

    public static CompanyProfileForm From(ClientDto c) => new()
    {
        RowVersion = c.RowVersion, Name = c.Name, LegalName = c.LegalName, ContactEmail = c.ContactEmail, ContactPhone = c.ContactPhone,
        AddressLine1 = c.AddressLine1, AddressLine2 = c.AddressLine2, City = c.City, State = c.State, PostalCode = c.PostalCode, Country = c.Country,
        Website = c.Website, Industry = c.Industry, TimeZone = c.TimeZone,
    };

    public UpdateClientProfileRequest ToRequest() => new(
        Name.Trim(), Blank(LegalName), ContactEmail.Trim(), Blank(ContactPhone), Blank(AddressLine1), Blank(AddressLine2), Blank(City), Blank(State),
        Blank(PostalCode), Blank(Country)?.ToUpperInvariant(), Blank(Website), Blank(Industry), TimeZone.Trim(), RowVersion);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
