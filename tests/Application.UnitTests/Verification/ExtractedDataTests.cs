using System.Text.Json;
using NexaVerify.Application.Verification.Abstractions;

namespace NexaVerify.Application.UnitTests.Verification;

public class ExtractedDataTests
{
    [Fact]
    public void Extracted_data_round_trips_through_its_closed_discriminator_set()
    {
        ExtractedData pan = new PanData { PanNumber = "ABCDE1234F", HolderTypeCode = "P" };
        ExtractedData passport = new PassportData { PassportNumber = "N1234567", MrzPresent = true };
        ExtractedData bank = new BankStatementData { RuleSetId = "generic", Transactions = [new BankTransaction(1, new DateOnly(2025, 1, 1), null, "d", null, 1m, null, 9m, null)] };

        foreach (var data in new[] { pan, passport, bank })
        {
            var json = JsonSerializer.Serialize(data);
            JsonSerializer.Deserialize<ExtractedData>(json)!.GetType().ShouldBe(data.GetType());
        }

        JsonSerializer.Serialize(pan).ShouldContain("\"kind\":\"pan\"");
    }

    [Fact]
    public void Unknown_discriminators_are_not_deserialised()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ExtractedData>("{\"kind\":\"aadhaar\"}"));
    }

    [Fact]
    public void Values_never_reach_a_log_line_through_ToString()
    {
        new PanData { PanNumber = "ABCDE1234F", NameOnCard = "SECRET NAME" }.ToString().ShouldBe(nameof(PanData));
        new BankStatementData { AccountHolderName = "SECRET NAME" }.ToString().ShouldBe(nameof(BankStatementData));
        new DocumentUpload("secret-name.pdf", "application/pdf", [1, 2]).ToString().ShouldNotContain("secret");
        new TextLine("SECRET", 0.9m, null, null, null, null).ToString().ShouldNotContain("SECRET");
        new PanProviderRequest("ABCDE1234F", "SECRET", null, "c").ToString().ShouldNotContain("ABCDE");
    }
}
