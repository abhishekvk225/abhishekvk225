namespace NexaVerify.Domain.Billing;

/// <summary>
/// The last invoice number handed out in a calendar year. It is advanced by one atomic statement inside the transaction that marks the
/// order paid, so a rolled-back payment gives its number back: numbers are gapless per year.
/// </summary>
public sealed class InvoiceSequence
{
    public int Year { get; set; }

    public int LastNumber { get; set; }

    public static string Format(int year, int number) => $"INV-{year:D4}-{number:D6}";
}
