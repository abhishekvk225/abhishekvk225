namespace NexaVerify.Domain.Verification;

/// <summary>
/// Value object for a PAN / passport / account number shown in lists and stored in queryable columns.
/// Only the last characters survive; the full identifier lives solely in the encrypted result blob.
/// </summary>
public readonly record struct MaskedIdentifier
{
    public const int MaxLength = 32;
    private const char Fill = 'X';

    private MaskedIdentifier(string value)
    {
        Value = value;
    }

    public string Value { get; }

    /// <summary>Keeps the last <paramref name="visible"/> characters (never more than a third of the value) and masks the rest.</summary>
    public static MaskedIdentifier From(string? identifier, int visible = 4)
    {
        var normalised = new string((identifier ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalised.Length == 0)
        {
            return new MaskedIdentifier(string.Empty);
        }

        var keep = Math.Clamp(visible, 0, Math.Max(0, normalised.Length / 3));
        var masked = new string(Fill, Math.Min(normalised.Length - keep, MaxLength - keep)) + normalised[^keep..];
        return new MaskedIdentifier(masked.Length > MaxLength ? masked[^MaxLength..] : masked);
    }

    public override string ToString() => Value;
}
