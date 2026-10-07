namespace NexaVerify.Web.ComponentTests;

public class StatusChipTests : UiTestBase
{
    [Theory]
    [InlineData(StatusKind.Client, "Active", "Active", Color.Success)]
    [InlineData(StatusKind.Client, "suspended", "Suspended", Color.Warning)]
    [InlineData(StatusKind.License, "Exhausted", "Used up", Color.Error)]
    [InlineData(StatusKind.License, "EXPIRED", "Expired", Color.Error)]
    [InlineData(StatusKind.ApiKey, "Revoked", "Revoked", Color.Error)]
    [InlineData(StatusKind.Outcome, "NoMatch", "No match", Color.Info)]
    [InlineData(StatusKind.Outcome, "no_face_detected", "No face found", Color.Warning)]
    [InlineData(StatusKind.Outcome, "Matched", "Match", Color.Success)]
    public void Maps_status_to_label_and_colour(StatusKind kind, string value, string label, Color color)
    {
        var style = StatusMap.Resolve(kind, value);

        style.Label.ShouldBe(label);
        style.Color.ShouldBe(color);
        style.Icon.ShouldNotBeNullOrWhiteSpace("colour must never be the only signal");
    }

    [Fact]
    public void Unknown_value_gets_neutral_style_not_an_exception()
    {
        var style = StatusMap.Resolve(StatusKind.License, "SomethingNew");

        style.Color.ShouldBe(Color.Default);
        style.Label.ShouldBe("Something new");
        StatusMap.Resolve(StatusKind.Client, null).Label.ShouldBe("Unknown");
    }

    [Fact]
    public void Every_known_status_has_an_icon_and_label()
    {
        foreach (var (kind, values) in new (StatusKind, string[])[]
        {
            (StatusKind.Client, ["Active", "Pending", "Suspended", "Inactive"]),
            (StatusKind.License, ["Draft", "Active", "ExpiringSoon", "Exhausted", "Expired", "Suspended", "Cancelled"]),
            (StatusKind.ApiKey, ["Active", "Expired", "Revoked"]),
            (StatusKind.Outcome, ["Enrolled", "Matched", "NoMatch", "NoFaceDetected", "MultipleFaces", "LowQuality", "ProviderError", "Rejected"]),
        })
        {
            foreach (var v in values)
            {
                var s = StatusMap.Resolve(kind, v);
                s.Icon.ShouldNotBeNullOrWhiteSpace($"{kind}/{v}");
                s.Icon.ShouldNotBe(Icons.Material.Outlined.HelpOutline, $"{kind}/{v} should be mapped, not fall back");
            }
        }
    }

    [Fact]
    public void Renders_label_text()
    {
        var cut = Render<StatusChip>(p => p.Add(x => x.Kind, StatusKind.License).Add(x => x.Value, "Exhausted"));

        cut.Markup.ShouldContain("Used up");
    }
}
