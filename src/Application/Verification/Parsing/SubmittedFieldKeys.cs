namespace NexaVerify.Application.Verification.Parsing;

/// <summary>
/// Keys of <c>ModuleContext.SubmittedFields</c> (the camelCase names of the submit DTO properties). Dates are ISO <c>yyyy-MM-dd</c>.
/// The submission core writes them, the module processors read them; nothing else may invent keys.
/// </summary>
public static class SubmittedFieldKeys
{
    public const string AccountHolderName = "accountHolderName";
    public const string BankName = "bankName";
    public const string AccountNumberLast4 = "accountNumberLast4";
    public const string PeriodFrom = "periodFrom";
    public const string PeriodTo = "periodTo";

    public const string PanNumber = "panNumber";
    public const string FullName = "fullName";
    public const string DateOfBirth = "dateOfBirth";
    public const string FatherName = "fatherName";

    public const string PassportNumber = "passportNumber";
    public const string Nationality = "nationality";
    public const string DateOfIssue = "dateOfIssue";
    public const string DateOfExpiry = "dateOfExpiry";

    public static string? Get(IReadOnlyDictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public static DateOnly? GetDate(IReadOnlyDictionary<string, string?> fields, string key) =>
        DateOnly.TryParseExact(Get(fields, key), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
