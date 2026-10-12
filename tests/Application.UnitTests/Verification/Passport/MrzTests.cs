using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Passport;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Application.UnitTests.Verification.Passport;

public class MrzTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    [Theory]
    [InlineData("L898902C3", '6')] // ICAO 9303 specimen document number
    [InlineData("740812", '2')]
    [InlineData("120415", '9')]
    [InlineData("ZE184226B<<<<<", '1')]
    public void Check_digit_matches_the_icao_specimen(string field, char digit)
    {
        MrzCheckDigit.Compute(field).ShouldBe(digit - '0');
        MrzCheckDigit.IsValid(field, digit).ShouldBeTrue();
        MrzCheckDigit.IsValid(field, (char)(((digit - '0' + 1) % 10) + '0')).ShouldBeFalse();
    }

    [Fact]
    public void Check_digit_rejects_invalid_characters_and_non_digits()
    {
        MrzCheckDigit.Compute("AB-1").ShouldBe(-1);
        MrzCheckDigit.IsValid("123", '<').ShouldBeFalse();
        MrzCheckDigit.Compute(string.Empty).ShouldBe(0);
    }

    [Fact]
    public void Icao_specimen_parses_with_all_check_digits_valid()
    {
        var r = MrzTd3Parser.Parse(["VISUAL ZONE TEXT", SampleDocuments.IcaoSpecimenLine1, SampleDocuments.IcaoSpecimenLine2], Today);
        r.Status.ShouldBe(MrzParseStatus.Parsed);
        r.OcrCorrected.ShouldBeFalse();
        r.Checks.ShouldBe(new MrzCheckStates(MrzCheckState.Valid, MrzCheckState.Valid, MrzCheckState.Valid, MrzCheckState.Valid, MrzCheckState.Valid));
        var f = r.Fields!;
        f.Surname.ShouldBe("ERIKSSON");
        f.GivenNames.ShouldBe("ANNA MARIA");
        f.DocumentNumber.ShouldBe("L898902C3");
        f.IssuingState.ShouldBe("UTO");
        f.Nationality.ShouldBe("UTO");
        f.DateOfBirth.ShouldBe(new DateOnly(1974, 8, 12)); // 2074 would be in the future -> 19YY
        f.DateOfExpiry.ShouldBe(new DateOnly(2012, 4, 15));
        f.Sex.ShouldBe("F");
        f.PersonalNumber.ShouldBe("ZE184226B");
    }

    [Fact]
    public void A_wrong_check_digit_is_reported_per_field()
    {
        var bad = SampleDocuments.IcaoSpecimenLine2.Remove(19, 1).Insert(19, "3"); // DOB check 2 -> 3
        var r = MrzTd3Parser.Parse([SampleDocuments.IcaoSpecimenLine1, bad], Today);
        r.Checks.DateOfBirth.ShouldBe(MrzCheckState.Invalid);
        r.Checks.DocumentNumber.ShouldBe(MrzCheckState.Valid);
        r.Checks.DateOfExpiry.ShouldBe(MrzCheckState.Valid);
        r.Checks.Composite.ShouldBe(MrzCheckState.Invalid); // the composite covers the DOB check digit too
    }

    [Fact]
    public void Altered_data_breaks_the_composite_digit()
    {
        var altered = SampleDocuments.IcaoSpecimenLine2.Remove(13, 1).Insert(13, "8"); // DOB digit changed
        var r = MrzTd3Parser.Parse([SampleDocuments.IcaoSpecimenLine1, altered], Today);
        r.Checks.DateOfBirth.ShouldBe(MrzCheckState.Invalid);
        r.Checks.Composite.ShouldBe(MrzCheckState.Invalid);
    }

    [Fact]
    public void Ocr_confusions_are_repaired_and_reported()
    {
        // O for 0 inside the date of birth and the expiry, S for 5 in the composite zone.
        var noisy = SampleDocuments.IcaoSpecimenLine2.Replace("740812", "74O812").Replace("120415", "12O415");
        var r = MrzTd3Parser.Parse([SampleDocuments.IcaoSpecimenLine1, noisy], Today);
        r.OcrCorrected.ShouldBeTrue();
        r.Checks.DateOfBirth.ShouldBe(MrzCheckState.Valid);
        r.Checks.DateOfExpiry.ShouldBe(MrzCheckState.Valid);
        r.Fields!.DateOfBirth.ShouldBe(new DateOnly(1974, 8, 12));
    }

    [Fact]
    public void Chevron_lookalikes_and_spaces_are_normalised()
    {
        var spaced = SampleDocuments.IcaoSpecimenLine1.Replace('<', '«').Insert(10, " ");
        var r = MrzTd3Parser.Parse([spaced, SampleDocuments.IcaoSpecimenLine2], Today);
        r.Status.ShouldBe(MrzParseStatus.Parsed);
        r.Fields!.Surname.ShouldBe("ERIKSSON");
    }

    [Fact]
    public void Generated_lines_are_valid_and_empty_personal_number_is_not_present()
    {
        var (l1, l2) = SampleTexts.Mrz();
        var r = MrzTd3Parser.Parse([l1, l2], Today);
        r.Checks.DocumentNumber.ShouldBe(MrzCheckState.Valid);
        r.Checks.PersonalNumber.ShouldBe(MrzCheckState.NotPresent);
        r.Checks.Composite.ShouldBe(MrzCheckState.Valid);
        r.Fields!.DateOfBirth.ShouldBe(new DateOnly(1990, 8, 15));
        r.Fields.DateOfExpiry.ShouldBe(new DateOnly(2035, 12, 31));
    }

    [Fact]
    public void Impossible_calendar_dates_become_null()
    {
        var (l1, l2) = SampleTexts.Mrz(dob: "901340");
        MrzTd3Parser.Parse([l1, l2], Today).Fields!.DateOfBirth.ShouldBeNull();
    }

    [Fact]
    public void Td1_and_td2_are_reported_as_unsupported()
    {
        var td2 = new[] { "I<UTOD231458907<<<<<<<<<<<<<<<", "7408122F1204159UTO<<<<<<<<<<<6" };
        var td1 = new[] { "I<UTOD231458907<<<<<<<<<<<<<<<", "7408122F1204159UTO<<<<<<<<<<<6", "ERIKSSON<<ANNA<MARIA<<<<<<<<<<" };
        MrzTd3Parser.Parse(td1, Today).Status.ShouldBe(MrzParseStatus.UnsupportedFormat);
        _ = td2;
    }

    [Theory]
    [InlineData("")]
    [InlineData("just some words on a page")]
    [InlineData("P<UTO")]
    public void No_mrz_is_not_found(string line)
    {
        MrzTd3Parser.Parse([line, "another line"], Today).Status.ShouldBe(MrzParseStatus.NotFound);
        MrzTd3Parser.Parse([], Today).Checks.Composite.ShouldBe(MrzCheckState.NotPresent);
    }

    [Fact]
    public void Result_never_prints_personal_data()
    {
        var r = MrzTd3Parser.Parse([SampleDocuments.IcaoSpecimenLine1, SampleDocuments.IcaoSpecimenLine2], Today);
        r.ToString().ShouldNotContain("ERIKSSON");
        r.Fields!.ToString().ShouldNotContain("L898902C3");
    }

    [Fact]
    public void Viz_reader_reads_labelled_values()
    {
        var viz = PassportVizReader.Read(SampleTexts.PassportPage());
        viz.PassportNumber.ShouldBe("X1234567");
        viz.Surname.ShouldBe("TESTPERSON");
        viz.GivenNames.ShouldBe("ASHA");
        viz.DateOfBirth.ShouldBe(new DateOnly(1990, 8, 15));
        viz.DateOfIssue.ShouldBe(new DateOnly(2026, 1, 1));
        viz.DateOfExpiry.ShouldBe(new DateOnly(2035, 12, 31));
        viz.Sex.ShouldBe("F");
        PassportVizReader.Read(["12 JAN/JAN 2020"]).IsEmpty.ShouldBeTrue();
        PassportVizReader.Read(["Date of Expiry 15 MAR/MAR 2030"]).DateOfExpiry.ShouldBe(new DateOnly(2030, 3, 15));
    }
}
