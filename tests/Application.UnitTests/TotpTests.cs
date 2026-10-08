using System.Text;
using NexaVerify.Application.Identity;

namespace NexaVerify.Application.UnitTests;

/// <summary>RFC 6238 (TOTP) with the 6-digit truncation authenticator apps use, and RFC 4648 base32.</summary>
public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890"); // the RFC 6238 SHA-1 test secret

    // RFC 6238 appendix B (SHA-1), last six of the published 8-digit values.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Codes_match_the_RFC_6238_test_vectors(long unixSeconds, string expected)
    {
        Totp.Compute(RfcSecret, unixSeconds / Totp.StepSeconds).ShouldBe(expected);
    }

    [Fact]
    public void StepAt_counts_30_second_steps_from_the_epoch()
    {
        Totp.StepAt(DateTime.UnixEpoch).ShouldBe(0);
        Totp.StepAt(DateTime.UnixEpoch.AddSeconds(29)).ShouldBe(0);
        Totp.StepAt(DateTime.UnixEpoch.AddSeconds(30)).ShouldBe(1);
        Totp.StepAt(new DateTime(2033, 5, 18, 3, 33, 20, DateTimeKind.Utc)).ShouldBe(2000000000L / 30);
    }

    [Fact]
    public void A_code_is_accepted_one_step_either_side_and_reports_which_step_matched()
    {
        var now = DateTime.UnixEpoch.AddSeconds(1111111111);
        var step = Totp.StepAt(now);

        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step), now).ShouldBe(step);
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step - 1), now).ShouldBe(step - 1);
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step + 1), now).ShouldBe(step + 1);
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step - 2), now).ShouldBeNull();
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step + 2), now).ShouldBeNull();
        Totp.TryMatch(RfcSecret, Totp.Compute(RfcSecret, step + 1), now, window: 0).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("١٢٣٤٥٦")] // non-ASCII digits are not digits
    [InlineData("      ")]
    public void Malformed_codes_never_match(string code)
    {
        Totp.TryMatch(RfcSecret, code, DateTime.UtcNow).ShouldBeNull();
    }

    [Fact]
    public void A_different_secret_does_not_match()
    {
        var now = DateTime.UtcNow;
        var other = Encoding.ASCII.GetBytes("98765432109876543210");

        Totp.TryMatch(other, Totp.Compute(RfcSecret, Totp.StepAt(now)), now).ShouldBeNull();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_the_RFC_4648_vectors_without_padding(string text, string encoded)
    {
        Base32.Encode(Encoding.ASCII.GetBytes(text)).ShouldBe(encoded);
        Encoding.ASCII.GetString(Base32.Decode(encoded)!).ShouldBe(text);
    }

    [Fact]
    public void Base32_decoding_ignores_case_spaces_and_padding_and_rejects_foreign_characters()
    {
        Base32.Decode("mzxw 6ytb-oi======").ShouldBe(Encoding.ASCII.GetBytes("foobar"));
        Base32.Decode("MZXW1").ShouldBeNull(); // '1' is not in the alphabet
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Totp.SecretBytes);
        Base32.Decode(Base32.Encode(random)).ShouldBe(random);
        Base32.Encode(random).Length.ShouldBe(32);
    }
}
