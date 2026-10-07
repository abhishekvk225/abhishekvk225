namespace NexaVerify.Web.ComponentTests;

public class StatCardTests : UiTestBase
{
    [Fact]
    public void Shows_title_and_value()
    {
        var cut = Render<StatCard>(p => p.Add(x => x.Title, "Credits left").Add(x => x.Value, "842"));

        cut.Find("[data-testid=stat-value]").TextContent.ShouldBe("842");
        cut.Markup.ShouldContain("Credits left");
        cut.FindAll("[data-testid=stat-delta]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(12.5, true, "nv-delta-good", "12.5%")]
    [InlineData(-4.0, true, "nv-delta-bad", "4%")]
    [InlineData(3.0, false, "nv-delta-bad", "3%")]
    [InlineData(-3.0, false, "nv-delta-good", "3%")]
    [InlineData(0.0, true, "nv-delta-flat", "0%")]
    public void Delta_colour_follows_whether_higher_is_better(double delta, bool higherIsBetter, string cssClass, string text)
    {
        var cut = Render<StatCard>(p => p
            .Add(x => x.Title, "T").Add(x => x.Value, "1")
            .Add(x => x.DeltaPercent, delta).Add(x => x.HigherIsBetter, higherIsBetter));

        var el = cut.Find("[data-testid=stat-delta]");
        el.ClassList.ShouldContain(cssClass);
        el.TextContent.Trim().ShouldBe(text);
        el.GetAttribute("aria-label").ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Loading_shows_skeleton_instead_of_value()
    {
        var cut = Render<StatCard>(p => p.Add(x => x.Title, "T").Add(x => x.Value, "99").Add(x => x.Loading, true));

        cut.FindAll("[data-testid=skeleton-card]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=stat-value]").ShouldBeEmpty();
    }

    [Fact]
    public void Renders_markup_characters_in_title_as_text()
    {
        var cut = Render<StatCard>(p => p.Add(x => x.Title, "<img src=x onerror=alert(1)>").Add(x => x.Value, "1"));

        cut.FindAll("img").ShouldBeEmpty();
        cut.Markup.ShouldContain("&lt;img");
    }
}
