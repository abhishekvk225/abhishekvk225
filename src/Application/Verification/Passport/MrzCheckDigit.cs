namespace NexaVerify.Application.Verification.Passport;

/// <summary>ICAO 9303 check digit: values 0-9 and A-Z=10..35 and '&lt;'=0, weights 7-3-1 repeating, sum modulo 10.</summary>
public static class MrzCheckDigit
{
    private static readonly int[] Weights = [7, 3, 1];

    /// <summary>Returns -1 when the field contains a character outside <c>[0-9A-Z&lt;]</c>.</summary>
    public static int Compute(ReadOnlySpan<char> field)
    {
        var sum = 0;
        for (var i = 0; i < field.Length; i++)
        {
            var v = ValueOf(field[i]);
            if (v < 0)
            {
                return -1;
            }

            sum += v * Weights[i % 3];
        }

        return sum % 10;
    }

    public static bool IsValid(ReadOnlySpan<char> field, char digit)
    {
        var expected = Compute(field);
        return expected >= 0 && digit is >= '0' and <= '9' && digit - '0' == expected;
    }

    private static int ValueOf(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        '<' => 0,
        _ => -1,
    };
}
