using NexaVerify.Web.Pages.Marketing;

namespace NexaVerify.Web.ComponentTests;

public class VerificationMarketingTests : PublicSiteTestBase
{
    private static readonly System.Text.RegularExpressions.Regex Overclaim =
        new(@"\b(authentic\w*|genuine\w*|guarantee\w*|fraud-proof|tamper-proof)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [Fact]
    public void Features_describes_the_three_available_checks_and_labels_the_rest_upcoming()
    {
        var cut = Render<Features>();

        var available = cut.Find("[data-testid=doc-available]").TextContent;
        available.ShouldContain("Bank statements");
        available.ShouldContain("PAN");
        available.ShouldContain("Passports");
        cut.Find("[data-testid=doc-upcoming-title]").TextContent.ShouldBe("Upcoming");
        var upcoming = cut.Markup;
        upcoming.ShouldContain("Aadhaar");
        upcoming.ShouldContain("not available yet, and not charged for");
        cut.FindAll("h1").Count.ShouldBe(1);
    }

    [Fact]
    public void The_copy_is_honest_about_what_checks_and_providers_can_do()
    {
        var cut = Render<Features>();

        var note = cut.Find("[data-testid=doc-note]").TextContent;
        note.ShouldContain("do not prove that a document is real");
        note.ShouldContain("approved provider");
        Overclaim.IsMatch(cut.Find("#documents").TextContent).ShouldBeFalse();
    }

    [Fact]
    public void Pricing_says_document_checks_use_credits_and_failures_are_free_without_promising_prices()
    {
        var cut = Render<Pricing>();

        var copy = cut.Find("[data-testid=doc-credits-copy]").TextContent;
        copy.ShouldContain("same balance");
        copy.ShouldContain("unreadable documents and failed checks are free");
        copy.ShouldContain("Upcoming document types are not charged for");
        Overclaim.IsMatch(copy).ShouldBeFalse();
        cut.FindAll("h1").Count.ShouldBe(1);
    }
}
