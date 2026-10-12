using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Pan;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Application.UnitTests.Verification.Pan;

public class PanRulesTests
{
    private static readonly PanRuleOptions Options = new();

    [Theory]
    [InlineData("ABCPT1234F", true, "P")]
    [InlineData("  abcpt1234f ", true, "P")] // case and whitespace
    [InlineData("ABCPT-1234-F", true, "P")] // separators
    [InlineData("ABCCT1234F", true, "C")]
    [InlineData("ABCPT1234", false, null)] // too short
    [InlineData("ABCPT12345", false, null)]
    [InlineData("1BCPT1234F", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void Format(string? input, bool valid, string? holderType)
    {
        var v = PanRules.Validate(input, Options);
        v.IsFormatValid.ShouldBe(valid);
        v.HolderTypeCode.ShouldBe(holderType);
        v.OcrCorrected.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_holder_type_is_reported()
    {
        var v = PanRules.Validate("ABCXT1234F", Options);
        v.IsFormatValid.ShouldBeTrue();
        v.HolderTypeKnown.ShouldBeFalse();
        v.HolderTypeName.ShouldBeNull();
    }

    [Theory]
    [InlineData("P", "Individual (Person)")]
    [InlineData("C", "Company")]
    [InlineData("T", "Trust")]
    public void Holder_type_table(string code, string name)
    {
        var v = PanRules.Validate($"ABC{code}T1234F", Options);
        v.HolderTypeKnown.ShouldBeTrue();
        v.HolderTypeName.ShouldBe(name);
    }

    [Fact]
    public void Holder_types_are_configurable()
    {
        PanRules.Validate("ABCPT1234F", new PanRuleOptions { HolderTypes = ["C"] }).HolderTypeKnown.ShouldBeFalse();
    }

    [Fact]
    public void Pattern_is_configurable()
    {
        PanRules.Validate("12345", new PanRuleOptions { Pattern = "^[0-9]{5}$" }).IsFormatValid.ShouldBeTrue();
    }

    [Fact]
    public void Ocr_repair_only_when_allowed_and_only_when_the_result_matches()
    {
        PanRules.Validate("ABCPT1Z34F", Options).IsFormatValid.ShouldBeFalse();
        var repaired = PanRules.Validate("ABCPT1Z34F", Options, allowOcrRepair: true);
        repaired.IsFormatValid.ShouldBeTrue();
        repaired.OcrCorrected.ShouldBeTrue();
        repaired.Normalized.ShouldBe("ABCPT1234F");

        PanRules.Validate("ABCPT1234F", Options, allowOcrRepair: true).OcrCorrected.ShouldBeFalse();
        PanRules.Validate("AB@PT1234F", Options, allowOcrRepair: true).IsFormatValid.ShouldBeFalse();
    }

    [Fact]
    public void Mask_keeps_only_the_tail()
    {
        PanRules.Mask("ABCPT1234F").ShouldNotContain("ABCPT");
        PanRules.Mask("ABCPT1234F").ShouldEndWith("F");
    }

    [Fact]
    public void Validation_record_never_prints_the_number() =>
        PanRules.Validate("ABCPT1234F", Options).ToString().ShouldNotContain("ABCPT");

    [Fact]
    public void Document_reader_extracts_labelled_fields_from_a_synthetic_card()
    {
        var read = PanDocumentReader.Read(SampleTexts.PanCard() is var l ? SampleDocuments.TextValue(l) : null!, Options);
        read.Data.PanNumber.ShouldBe(SampleTexts.PanNumber);
        read.Data.NameOnCard.ShouldBe("ASHA TESTPERSON");
        read.Data.FatherName.ShouldBe("RAJ TESTPERSON");
        read.Data.DateOfBirth.ShouldBe(new DateOnly(1990, 8, 15));
        read.Data.HolderTypeCode.ShouldBe("P");
    }

    [Fact]
    public void Document_reader_reports_ocr_correction()
    {
        var read = PanDocumentReader.Read(SampleDocuments.TextValue(SampleTexts.PanCard(pan: "ABCPT1Z34F")), Options);
        read.Data.PanNumber.ShouldBe("ABCPT1234F");
        read.Data.NumberOcrCorrected.ShouldBeTrue();
    }

    [Fact]
    public void Document_reader_handles_inline_labels_and_garbage()
    {
        var read = PanDocumentReader.Read(SampleDocuments.TextValue("Name: ASHA TESTPERSON", "DOB: 15-08-1990", "~~~~~", new string('x', 500)), Options);
        read.Data.NameOnCard.ShouldBe("ASHA TESTPERSON");
        read.Data.DateOfBirth.ShouldBe(new DateOnly(1990, 8, 15));
        read.Data.PanNumber.ShouldBeNull();
        PanDocumentReader.Read(SampleDocuments.TextValue("nothing useful here"), Options).Data.PanNumber.ShouldBeNull();
    }
}
