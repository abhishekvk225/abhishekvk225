using System.ComponentModel.DataAnnotations;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Web.Services;

/// <summary>Fields every check has. Rules mirror the API validators so people see problems inline; the API stays authoritative.</summary>
public abstract class VerificationFormBase
{
    [StringLength(100, ErrorMessage = "Use at most 100 characters.")]
    public string? ExternalRef { get; set; }

    [Required(ErrorMessage = "Enter where the person's consent is recorded (for example a signed form number)."), StringLength(100, ErrorMessage = "Use at most 100 characters.")]
    public string? ConsentReference { get; set; }

    protected static DateOnly? D(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);
}

public sealed class BankStatementForm : VerificationFormBase, IValidatableObject
{
    [StringLength(150, ErrorMessage = "Use at most 150 characters.")]
    public string? AccountHolderName { get; set; }

    [StringLength(100, ErrorMessage = "Use at most 100 characters.")]
    public string? BankName { get; set; }

    [RegularExpression(@"^\d{4}$", ErrorMessage = "Enter exactly the last 4 digits.")]
    public string? AccountNumberLast4 { get; set; }

    public DateTime? PeriodFrom { get; set; }

    public DateTime? PeriodTo { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PeriodFrom is { } from && PeriodTo is { } to && from > to)
        {
            yield return new ValidationResult("The period must start before it ends.", [nameof(PeriodFrom)]);
        }
    }

    public SubmitBankStatementRequest ToRequest() =>
        new(ExternalRef.NullIfBlank(), ConsentReference.NullIfBlank(), AccountHolderName.NullIfBlank(), BankName.NullIfBlank(), AccountNumberLast4.NullIfBlank(), D(PeriodFrom), D(PeriodTo));
}

public sealed class PanForm : VerificationFormBase
{
    [RegularExpression(@"^\s*[A-Za-z]{5}[0-9]{4}[A-Za-z]\s*$", ErrorMessage = "A PAN has 5 letters, 4 digits and 1 letter, for example ABCDE1234F.")]
    public string? PanNumber { get; set; }

    [Required(ErrorMessage = "Enter the name as it should appear on the PAN."), StringLength(150, ErrorMessage = "Use at most 150 characters.")]
    public string? FullName { get; set; }

    public DateTime? DateOfBirth { get; set; }

    [StringLength(150, ErrorMessage = "Use at most 150 characters.")]
    public string? FatherName { get; set; }

    public SubmitPanRequest ToRequest() =>
        new(ExternalRef.NullIfBlank(), ConsentReference.NullIfBlank(), PanNumber.NullIfBlank()?.ToUpperInvariant(), FullName.NullIfBlank(), D(DateOfBirth), FatherName.NullIfBlank());
}

public sealed class PassportForm : VerificationFormBase, IValidatableObject
{
    [RegularExpression(@"^\s*[A-Za-z0-9]{6,9}\s*$", ErrorMessage = "A passport number has 6 to 9 letters and digits.")]
    public string? PassportNumber { get; set; }

    [StringLength(150, ErrorMessage = "Use at most 150 characters.")]
    public string? FullName { get; set; }

    public DateTime? DateOfBirth { get; set; }

    [RegularExpression(@"^\s*[A-Za-z]{3}\s*$", ErrorMessage = "Use the 3-letter country code, for example IND.")]
    public string? Nationality { get; set; }

    public DateTime? DateOfIssue { get; set; }

    public DateTime? DateOfExpiry { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DateOfIssue is { } issue && DateOfExpiry is { } expiry && issue >= expiry)
        {
            yield return new ValidationResult("The issue date must be before the expiry date.", [nameof(DateOfIssue)]);
        }
    }

    public SubmitPassportRequest ToRequest() =>
        new(ExternalRef.NullIfBlank(), ConsentReference.NullIfBlank(), PassportNumber.NullIfBlank()?.ToUpperInvariant(), FullName.NullIfBlank(), D(DateOfBirth),
            Nationality.NullIfBlank()?.ToUpperInvariant(), D(DateOfIssue), D(DateOfExpiry));
}

/// <summary>Filters of the history page.</summary>
public sealed class VerificationHistoryFilter
{
    public string? Type { get; set; }

    public string? Status { get; set; }

    public bool ReviewOnly { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public bool IsActive => Type is not null || Status is not null || ReviewOnly || From is not null || To is not null;

    public void Clear() => (Type, Status, ReviewOnly, From, To) = (null, null, false, null, null);
}

/// <summary>Editing the platform price of one verification step.</summary>
public sealed class VerificationCostForm
{
    public required string Operation { get; init; }

    [Range(0, 1000, ErrorMessage = "Use a number between 0 and 1,000.")]
    public int Credits { get; set; }

    [Required]
    public string ChargePolicy { get; set; } = "OnCompleted";

    [Required(ErrorMessage = "Say why the price is changing. It is kept in the audit log."), StringLength(300, ErrorMessage = "Use at most 300 characters.")]
    public string? Reason { get; set; }
}
