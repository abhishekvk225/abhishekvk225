using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Matching;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.UnitTests.Verification.Matching;

public class NameMatcherTests
{
    private static readonly NameMatcher Matcher = new(Options.Create(new VerificationOptions()));

    [Theory]
    [InlineData("Asha Testperson", "ASHA TESTPERSON", FieldMatchOutcome.Match)]
    [InlineData("Testperson Asha", "ASHA TESTPERSON", FieldMatchOutcome.Match)] // order insensitive
    [InlineData("Mrs. Asha Testperson", "ASHA TESTPERSON", FieldMatchOutcome.Match)] // honorific dropped
    [InlineData("A Testperson", "ASHA TESTPERSON", FieldMatchOutcome.Match)] // initial
    [InlineData("Asha K Testperson", "ASHA TESTPERSON", FieldMatchOutcome.Match)] // one extra middle token
    [InlineData("José Müller", "JOSE MULLER", FieldMatchOutcome.Match)] // diacritics
    [InlineData("Asha  Test-person", "ASHA TEST PERSON", FieldMatchOutcome.Match)] // separators
    [InlineData("Krishna Iyer", "KRISHNAA IYER", FieldMatchOutcome.Match)] // transliteration (doubled vowel)
    [InlineData("Vikram Shah", "WIKRAM SHAH", FieldMatchOutcome.Match)] // transliteration (V/W)
    [InlineData("Asha", "ASHA KAPOOR TESTPERSON", FieldMatchOutcome.PartialMatch)] // token subset
    [InlineData("Asha Testperson", "ASHA TESTPERSAN", FieldMatchOutcome.Match)] // one typo in a long token stays above the threshold
    [InlineData("Asha Testperson", "ASHA TESTPRSAN", FieldMatchOutcome.PartialMatch)] // two typos
    [InlineData("A", "ASHA KAPOOR TESTPERSON", FieldMatchOutcome.PartialMatch)] // initial only is never a full match
    [InlineData("Asha Testperson", "RAVI SHARMA", FieldMatchOutcome.Mismatch)]
    [InlineData("Asha Testperson", "ASHA SHARMA", FieldMatchOutcome.Mismatch)]
    [InlineData(null, "ASHA", FieldMatchOutcome.NotProvided)]
    [InlineData("  ", "ASHA", FieldMatchOutcome.NotProvided)]
    [InlineData("MR", "ASHA", FieldMatchOutcome.NotProvided)] // only a title
    [InlineData("Asha", null, FieldMatchOutcome.NotExtracted)]
    [InlineData("Asha", "12345", FieldMatchOutcome.NotExtracted)] // nothing letter-like to compare
    public void Compare_matrix(string? submitted, string? extracted, FieldMatchOutcome expected) =>
        Matcher.Compare(submitted, extracted).Outcome.ShouldBe(expected);

    [Fact]
    public void Compare_is_deterministic_and_symmetric_for_equal_names()
    {
        var a = Matcher.Compare("Asha Testperson", "ASHA TESTPERSON");
        var b = Matcher.Compare("ASHA TESTPERSON", "Asha Testperson");
        a.ShouldBe(b);
        a.Score.ShouldBe(1m);
    }

    [Fact]
    public void Very_long_input_is_bounded()
    {
        var huge = string.Join(' ', Enumerable.Repeat("ABCDEFGHIJ", 5000));
        Should.NotThrow(() => Matcher.Compare(huge, huge));
    }
}
