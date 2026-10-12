using NexaVerify.Application.Verification.Passport;

namespace NexaVerify.TestSupport.Verification;

/// <summary>
/// Synthetic text of statements, PAN cards and passport data pages. Names, numbers and layouts are invented. The three "synthetic
/// layouts" are NOT claims about any real bank's format.
/// </summary>
public static class SampleTexts
{
    public const string Holder = "ASHA TESTPERSON";
    public const string AccountNumber = "9900112233";

    /// <summary>Generic layout, ascending dates, all balances consistent: opening 10,000.00 closing 20,500.00.</summary>
    public static string[] GenericStatement() =>
    [
        "Synthetic Test Statement",
        $"Account Holder: {Holder}",
        "Bank: Synthetic Test Bank",
        $"Account Number: {AccountNumber}",
        "Statement Period: 01/04/2024 to 30/04/2024",
        "Opening Balance: 10,000.00",
        "Date Narration Amount Balance",
        "02/04/2024 UPI SYNTH PAYEE ONE 500.00 9,500.00",
        "05/04/2024 SALARY SYNTHETIC EMPLOYER 25,000.00 34,500.00",
        "10/04/2024 ATM WITHDRAWAL 2,000.00 32,500.00",
        "continued narration line",
        "20/04/2024 RENT 12,000.00 20,500.00",
        "Page 1 of 1",
        "Closing Balance: 20,500.00",
    ];

    public static string[] LayoutA() =>
    [
        "SYNTHETIC BANK ALPHA",
        $"Account Holder: {Holder}",
        $"Account Number: {AccountNumber}",
        "Statement Period: 01-Apr-2024 to 30-Apr-2024",
        "Opening Balance: 10,000.00 Cr",
        "Date Particulars Amount Balance",
        "02-Apr-2024 UPI SYNTH PAYEE ONE 500.00 Dr 9,500.00 Cr",
        "05-Apr-2024 SALARY SYNTHETIC EMPLOYER 25,000.00 Cr 34,500.00 Cr",
        "Closing Balance: 34,500.00 Cr",
    ];

    public static string[] LayoutB() =>
    [
        "SYNTHETIC BANK BETA",
        $"Customer: {Holder}",
        $"A/C No: {AccountNumber}",
        "From: 01/04/24 To: 30/04/24",
        "Opening Bal: 10,000.00",
        "Date Ref Narration Debit Credit Balance",
        "02/04/24 REF000001 UPI SYNTH PAYEE ONE 500.00 - 9,500.00",
        "05/04/24 REF000002 SALARY SYNTHETIC EMPLOYER - 25,000.00 34,500.00",
        "Closing Bal: 34,500.00",
    ];

    public static string[] LayoutC() =>
    [
        "SYNTHETIC BANK GAMMA",
        $"Name: {Holder}",
        $"Account No.: {AccountNumber}",
        "Period: 01.04.2024 to 30.04.2024",
        "Opening Balance: 1,00,000.00",
        "Txn Date Value Date Narration Amount Balance",
        "02.04.2024 02.04.2024 UPI SYNTH PAYEE ONE 5,000.00 95,000.00",
        "05.04.2024 05.04.2024 SALARY SYNTHETIC EMPLOYER 2,50,000.00 3,45,000.00",
        "Closing Balance: 3,45,000.00",
    ];

    public const string PanNumber = "ABCPT1234F";

    public static string[] PanCard(string pan = PanNumber, string name = Holder, string father = "RAJ TESTPERSON", string dob = "15/08/1990") =>
    [
        "INCOME TAX DEPARTMENT",
        "GOVT. OF INDIA",
        "Permanent Account Number",
        pan,
        "Name",
        name,
        "Father's Name",
        father,
        "Date of Birth",
        dob,
        "Signature",
    ];

    /// <summary>Valid TD3 lines built with the same ICAO algorithm (so dates and numbers can be varied). Synthetic holder.</summary>
    public static (string Line1, string Line2) Mrz(
        string number = "X1234567", string country = "UTO", string surname = "TESTPERSON", string given = "ASHA", string dob = "900815",
        string sex = "F", string expiry = "351231", string personal = "")
    {
        var name = (surname + "<<" + given.Replace(' ', '<')).PadRight(39, '<');
        var line1 = ("P<" + country + name)[..44];
        var doc = number.PadRight(9, '<');
        var pers = personal.PadRight(14, '<');
        var persCheck = personal.Length == 0 ? "<" : MrzCheckDigit.Compute(pers).ToString();
        var body = doc + MrzCheckDigit.Compute(doc) + country + dob + MrzCheckDigit.Compute(dob) + sex + expiry + MrzCheckDigit.Compute(expiry) + pers + persCheck;
        var composite = MrzCheckDigit.Compute(body[..10] + body.Substring(13, 7) + body.Substring(21, 22));
        return (line1, body + composite);
    }

    public static string[] PassportPage(string number = "X1234567", string expiry = "31/12/2035", string issue = "01/01/2026", bool withMrz = true, string mrzExpiry = "351231")
    {
        var (l1, l2) = Mrz(number: number, expiry: mrzExpiry);
        var lines = new List<string>
        {
            "REPUBLIC OF UTOPIA PASSPORT",
            $"Passport No {number}",
            "Surname",
            "TESTPERSON",
            "Given Names",
            "ASHA",
            "Nationality",
            "UTO",
            "Date of Birth 15/08/1990",
            "Sex F",
            $"Date of Issue {issue}",
            $"Date of Expiry {expiry}",
        };
        if (withMrz)
        {
            lines.Add(l1);
            lines.Add(l2);
        }

        return [.. lines];
    }
}
