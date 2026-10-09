using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components.Web;
using NexaVerify.Web.Components.Marketing;
using NexaVerify.Web.Layouts;
using NexaVerify.Web.Pages.Marketing;
using NexaVerify.Web.Security;
using NotFoundPage = NexaVerify.Web.Pages.NotFound;

namespace NexaVerify.Web.ComponentTests;

/// <summary>Content, states and accessibility smoke checks for the marketing pages.</summary>
public class PublicPagesTests : PublicSiteTestBase
{
    public static TheoryData<Type> AllPages => new()
    {
        typeof(Home), typeof(Features), typeof(HowItWorks), typeof(Pricing), typeof(Developers), typeof(SecurityOverview),
        typeof(Contact), typeof(Signup), typeof(VerifyEmail), typeof(Terms), typeof(Privacy), typeof(NotFoundPage),
    };

    [Theory]
    [MemberData(nameof(AllPages))]
    public void Every_page_has_exactly_one_h1_labelled_fields_and_no_images_without_text(Type page)
    {
        var cut = Render(Build(page));

        cut.FindAll("h1").Count.ShouldBe(1, $"{page.Name} must have one h1");
        foreach (var input in cut.FindAll("input:not([type=hidden]), textarea, select"))
        {
            var id = input.GetAttribute("id");
            id.ShouldNotBeNullOrEmpty($"{page.Name}: every field needs an id");
            cut.FindAll($"label[for='{id}']").Count.ShouldBeGreaterThan(0, $"{page.Name}: field #{id} needs a label");
        }

        foreach (var svg in cut.FindAll("svg"))
        {
            (svg.GetAttribute("aria-hidden") == "true" || svg.GetAttribute("role") is "img" or "presentation").ShouldBeTrue($"{page.Name}: svg must be hidden or labelled");
        }

        cut.FindAll("img").Count.ShouldBe(0, "no external or inline raster images");
        cut.Markup.ShouldNotContain("http://");
    }

    [Theory]
    [MemberData(nameof(AllPages))]
    public void Pages_load_nothing_from_other_servers_unless_the_captcha_is_on(Type page)
    {
        var cut = Render(Build(page));

        cut.Markup.ShouldNotContain("src=\"http");
        cut.Markup.ShouldNotContain("href=\"https://fonts");
    }

    [Fact]
    public void Layout_has_the_landmarks_a_skip_link_and_a_theme_toggle()
    {
        var cut = Render<MarketingLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1>Hi</h1>"))));

        cut.Find("a.mk-skip").GetAttribute("href").ShouldBe("#main-content");
        cut.FindAll("header").Count.ShouldBe(1);
        cut.FindAll("main#main-content").Count.ShouldBe(1);
        cut.FindAll("footer").Count.ShouldBe(1);
        cut.FindAll("nav[aria-label]").Count.ShouldBeGreaterThanOrEqualTo(2);
        cut.Find("[data-testid=nav-signin]").GetAttribute("href").ShouldBe("/login");
        cut.Find("[data-testid=nav-signup]").GetAttribute("href").ShouldBe("/signup");
        cut.Find("[data-testid=theme-toggle]").GetAttribute("aria-label").ShouldNotBeNullOrEmpty();
        cut.Find("[data-testid=menu-toggle]").GetAttribute("aria-expanded").ShouldBe("false");
        cut.Markup.ShouldContain("2026");
    }

    [Theory]
    [InlineData(PortalKinds.Client, "/client")]
    [InlineData(PortalKinds.Admin, "/admin")]
    public void A_signed_in_visitor_sees_Go_to_dashboard_instead_of_sign_in(string portal, string expected)
    {
        var http = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(PortalClaims.Portal, portal)], "test")),
        };

        var cut = Render<CascadingValue<HttpContext>>(p => p
            .Add(c => c.Value, http)
            .AddChildContent<MarketingLayout>(l => l.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1>Hi</h1>")))));

        cut.Find("[data-testid=go-dashboard]").GetAttribute("href").ShouldBe(expected);
        cut.FindAll("[data-testid=nav-signin]").Count.ShouldBe(0);
        cut.FindAll("[data-testid=nav-signup]").Count.ShouldBe(0);
    }

    [Fact]
    public void Home_shows_the_sections_and_the_plans_from_the_api()
    {
        var cut = Render<Home>();

        cut.Find("h1").TextContent.ShouldContain("Know who is in front of you");
        cut.FindAll("[data-testid=steps] li").Count.ShouldBe(3);
        cut.FindAll("[data-testid=use-cases] article").Count.ShouldBe(4);
        cut.FindAll("[data-testid=faq] details").Count.ShouldBeGreaterThan(3);
        cut.FindAll("[data-testid=security-band]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=plans-teaser] [data-testid=plan-card]").Count.ShouldBe(2);
        cut.Find("[data-testid=trial-note]").TextContent.ShouldContain("200 credits for 14 days");
        cut.FindAll("[data-testid=hero-art]").Count.ShouldBe(1);
        cut.FindAll("[data-testid=product-mock]").Count.ShouldBe(1);
    }

    [Fact]
    public void Home_still_renders_when_the_api_is_down()
    {
        Api.Plans = () => ApiResult<IReadOnlyList<PublicPlanDto>>.Fail(Problem(503));
        Api.Config = () => ApiResult<PublicConfigDto>.Fail(Problem(503));

        var cut = Render<Home>();

        cut.FindAll("h1").Count.ShouldBe(1);
        cut.FindAll("[data-testid=plans-fallback]").Count.ShouldBe(1);
        cut.Find("[data-testid=trial-note]").TextContent.ShouldBe("Free trial available. No card needed.");
    }

    [Fact]
    public void Features_states_honestly_that_there_is_no_liveness_detection()
    {
        var cut = Render<Features>();

        var note = cut.Find("[data-testid=liveness-note]").TextContent;
        note.ShouldContain("does not include liveness");
        cut.Markup.ShouldNotContain("SOC 2");
        cut.Markup.ShouldNotContain("ISO 27001");
    }

    [Fact]
    public void Security_page_makes_no_certification_claims()
    {
        var text = Render<SecurityOverview>().Markup;

        text.ShouldContain("We do not claim any security or privacy certification");
        text.ShouldNotContain("SOC 2 compliant");
        text.ShouldNotContain("ISO 27001 certified");
        text.ShouldNotContain("HIPAA");
    }

    [Fact]
    public void Developers_page_reuses_the_in_app_samples_and_links_to_the_api_guide()
    {
        var cut = Render<Developers>();

        cut.Find("[data-testid=api-guide-link]").GetAttribute("href").ShouldBe("/client/api-docs");
        var code = string.Join("\n", cut.FindAll("pre code").Select(c => c.TextContent));
        code.ShouldContain("X-Api-Key");
        code.ShouldContain("/faces/enroll");
        code.ShouldContain("/faces/verify");
        code.ShouldContain("/faces/identify");
        code.ShouldContain("HMACSHA256");
        code.ShouldContain("YOUR_API_KEY");
        cut.FindAll("[data-testid=code-tabs]").Count.ShouldBeGreaterThanOrEqualTo(5);
        cut.FindAll("[data-testid=code-tabs] input[type=radio]").Select(r => r.GetAttribute("id")).Distinct().Count()
            .ShouldBe(cut.FindAll("[data-testid=code-tabs] input[type=radio]").Count, "radio ids must be unique across tab groups");
    }

    [Theory]
    [InlineData(typeof(Terms), "terms-body")]
    [InlineData(typeof(Privacy), "privacy-body")]
    public void Legal_pages_are_clearly_marked_as_drafts_and_cover_biometric_consent(Type page, string body)
    {
        var cut = Render(Build(page));

        cut.Find("[data-testid=draft-notice]").TextContent.ShouldContain("not been reviewed by a lawyer");
        var text = cut.Find($"[data-testid={body}]").TextContent;
        text.ShouldContain("consent");
        text.ShouldContain("[");
        if (page == typeof(Terms))
        {
            text.ShouldContain("informed, explicit and recorded consent");
        }
    }

    [Fact]
    public void Not_found_page_offers_ways_back()
    {
        var cut = Render<NotFoundPage>();

        cut.Find("h1").TextContent.ShouldContain("couldn't find that page");
        cut.FindAll("[data-testid=not-found-actions] a").Select(a => a.GetAttribute("href")).ShouldBe(["/", "/login", "/contact"]);
    }

    [Fact]
    public void Home_page_head_has_title_description_canonical_open_graph_and_json_ld()
    {
        var head = Render<HeadOutlet>();
        Render<Home>();

        head.WaitForAssertion(() => head.Markup.ShouldContain("rel=\"canonical\""));
        head.Find("link[rel=canonical]").GetAttribute("href").ShouldBe("https://www.example.test/");
        head.Find("meta[name=description]").GetAttribute("content")!.Length.ShouldBeGreaterThan(50);
        head.Find("meta[property='og:title']").ShouldNotBeNull();
        head.Find("meta[property='og:url']").GetAttribute("content").ShouldBe("https://www.example.test/");
        var ld = head.Find("script[type='application/ld+json']").TextContent;
        using var json = System.Text.Json.JsonDocument.Parse(ld);
        json.RootElement.GetProperty("@type").GetString().ShouldBe("Organization");
        json.RootElement.GetProperty("url").GetString().ShouldBe("https://www.example.test/");
    }

    [Fact]
    public void Json_ld_cannot_be_broken_out_of_its_script_element()
    {
        var cut = Render<OrganizationJsonLd>();

        cut.Instance.Json.ShouldNotContain("</");
        cut.Instance.Json.ShouldNotContain("<");
    }

    [Fact]
    public void Verify_and_not_found_pages_are_kept_out_of_search_results()
    {
        var head = Render<HeadOutlet>();
        Render<VerifyEmail>();

        head.WaitForAssertion(() => head.Markup.ShouldContain("noindex"));
    }

    private RenderFragment Build(Type page) => b =>
    {
        b.OpenComponent(0, page);
        b.CloseComponent();
    };
}
