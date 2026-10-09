using System.Net;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Mvc.Testing;
using BillingReturnPage = NexaVerify.Web.Pages.Client.BillingReturn;
using BuyCreditsPage = NexaVerify.Web.Pages.Client.BuyCredits;
using DevPayPage = NexaVerify.Web.Pages.Client.DevPay;
using OrdersPage = NexaVerify.Web.Pages.Client.BillingOrders;
using PricingPage = NexaVerify.Web.Pages.Marketing.Pricing;

namespace NexaVerify.Web.ComponentTests;

/// <summary>
/// The whole purchase on the portal's own pieces with the same stateful stand-in the demo mode uses (no payment provider, no API):
/// choose a pack, confirm, land on the simulator, pay, come back, wait for the order to settle, find it in the list.
/// </summary>
public class BuyReturnPaidFlowTests : ClientPageTestBase
{
    private readonly StubBillingStore _store;

    public BuyReturnPaidFlowTests()
    {
        _store = new StubBillingStore(Clock);
        Services.AddSingleton(_store);
        Services.AddSingleton<IBillingApiClient>(new StubBillingApiClient(_store, Clock));
        Services.AddSingleton<IDevBillingApiClient>(new StubDevBillingApiClient(_store, Clock));
        Environment.EnvironmentName = "Development";
        User.SignIn("Una Admin", "una@acme.test", "ClientAdmin", "Acme Corp", [WebPermissions.BillingRead, WebPermissions.BillingManage]);
    }

    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    [Theory]
    [InlineData("success", "return-paid", "Paid")]
    [InlineData("failed", "return-failed", "Failed")]
    [InlineData("cancelled", "return-failed", "Cancelled")]
    public async Task Buy_then_pay_then_return_ends_in_the_state_the_payment_partner_reported(string outcome, string panel, string finalStatus)
    {
        // 1. Buy credits: pick the first pack and agree.
        var providers = Providers();
        var buy = Render<BuyCreditsPage>();
        buy.WaitForAssertion(() => buy.FindAll("[data-testid=pack-card]").Count.ShouldBeGreaterThan(0));
        buy.FindAll("[data-testid=buy-pack]")[0].Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").Click();
        buy.WaitForAssertion(() => Nav.History.Count.ShouldBeGreaterThan(0));
        var payUrl = new Uri(Nav.History.First().Uri);
        payUrl.AbsolutePath.ShouldStartWith("/dev/pay/");
        var orderId = Guid.Parse(payUrl.AbsolutePath["/dev/pay/".Length..]);
        _store.Find(orderId)!.Status.ShouldBe(OrderStatuses.Pending, "the order exists but nothing is paid yet");

        // 2. The simulator stands in for the payment partner's page.
        var pay = Render<DevPayPage>(p => p.Add(x => x.OrderId, orderId));
        pay.WaitForAssertion(() => pay.FindAll("[data-testid=dev-pay-card]").Count.ShouldBe(1));
        pay.Find(outcome switch { "success" => "[data-testid=dev-pay-success]", "failed" => "[data-testid=dev-pay-fail]", _ => "[data-testid=dev-pay-cancel]" }).Click();
        pay.WaitForAssertion(() => Nav.History.First().Uri.ShouldContain("client/billing/return"));
        _store.Find(orderId)!.Status.ShouldBe(finalStatus);
        var returnUri = Nav.History.First().Uri;

        // 3. Back in the portal: the page reads the order from the API, whatever the address said.
        Nav.NavigateTo("/" + returnUri.TrimStart('/'));
        var back = Render<BillingReturnPage>();
        back.WaitForAssertion(() => back.FindAll($"[data-testid={panel}]").Count.ShouldBe(1));
        await Task.CompletedTask;

        // 4. The list shows it.
        var orders = Render<OrdersPage>();
        orders.WaitForAssertion(() => orders.FindAll($"table tbody tr [data-status='{OrderStatuses.Label(finalStatus)}']").Count.ShouldBeGreaterThan(0));
    }

    [Fact]
    public void A_payment_confirmed_while_the_person_waits_flips_the_page_to_paid_without_a_reload()
    {
        var order = _store.CreateOrder(_store.Packs[0].Id, Clock);
        Nav.NavigateTo($"/client/billing/return?order={order.Id}&result=success");
        var back = Render<BillingReturnPage>();
        back.WaitForAssertion(() => back.FindAll("[data-testid=return-pending]").Count.ShouldBe(1));

        _store.Simulate(order.Id, "success", Clock);
        Clock.Advance(TimeSpan.FromSeconds(1));

        back.WaitForAssertion(() => back.FindAll("[data-testid=return-paid]").Count.ShouldBe(1));
        back.Find("[data-testid=return-paid]").TextContent.ShouldContain("Your credits were added");
    }
}

/// <summary>The real portal host (stub clients, as in the demo mode): public pricing with real prices and the return-from-payment hop.</summary>
public class BillingPortalHostTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Ui:UseStubClients", "true");
        builder.UseSetting("Site:PublicBaseUrl", "https://www.example.test");
    });

    public void Dispose() => _factory.Dispose();

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task The_pricing_page_shows_real_prices_and_sends_visitors_through_sign_in_to_the_purchase_page()
    {
        using var client = Client();

        var html = await client.GetStringAsync("/pricing");

        html.ShouldContain("data-testid=\"packs\"");
        html.ShouldContain("$49.00");
        html.ShouldContain("$199.00");
        html.ShouldContain("href=\"/login?returnUrl=/client/billing/buy\"");
        html.ShouldContain("Buy credits");
        html.ShouldContain("Free trial", Case.Sensitive);
        html.ShouldContain("href=\"/contact\"", Case.Sensitive);
        html.ShouldNotContain("<script>alert");
    }

    [Fact]
    public async Task A_cross_site_arrival_on_the_return_address_gets_the_same_site_hop_not_a_login_page()
    {
        using var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Get, "/client/billing/return?order=00000000-0000-0000-0000-00000000d001&result=success");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("http-equiv=\"refresh\"");
        html.ShouldContain("hop=1");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'self'");
    }

    [Theory]
    [InlineData("/client/billing/buy")]
    [InlineData("/client/billing/orders")]
    [InlineData("/client/billing/profile")]
    [InlineData("/admin/billing/packs")]
    [InlineData("/admin/billing/orders")]
    [InlineData("/dev/pay/00000000-0000-0000-0000-00000000d001")]
    [InlineData("/bff/client/billing/orders/00000000-0000-0000-0000-00000000d001/invoice")]
    [InlineData("/bff/client/billing/orders/export.csv")]
    public async Task Everything_billing_needs_a_signed_in_user(string path)
    {
        using var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        var response = await client.SendAsync(request);

        ((int)response.StatusCode).ShouldBeInRange(301, 403, $"{path} must not be served to an anonymous visitor");
        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }
}

public class PricingPacksTests : PublicSiteTestBase
{
    private static readonly PublicPackDto Starter = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), "Starter pack", "For one site.", 5000, 365, 4900, "USD", null, ["Email support"]);
    private static readonly PublicPackDto Yen = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "Yen pack", null, 1000, 90, 150000, "JPY", null, null);

    [Fact]
    public void Packs_appear_with_real_exact_prices_and_a_buy_link_through_sign_in()
    {
        Api.Packs = () => ApiResult<IReadOnlyList<PublicPackDto>>.Ok([Starter, Yen]);

        var cut = Render<NexaVerify.Web.Pages.Marketing.Pricing>();

        var cards = cut.FindAll("[data-testid=pack-card]");
        cards.Count.ShouldBe(2);
        cards[0].QuerySelector("[data-testid=pack-price]")!.TextContent.ShouldBe("$49.00");
        cards[1].QuerySelector("[data-testid=pack-price]")!.TextContent.ShouldBe("¥150,000");
        cut.FindAll("[data-testid=pack-buy]").ShouldAllBe(a => a.GetAttribute("href") == "/login?returnUrl=/client/billing/buy");
        cut.Find("[data-testid=pack-buy]").GetAttribute("aria-label").ShouldBe("Buy credits: Starter pack");
    }

    [Fact]
    public void A_signed_in_client_goes_straight_to_the_purchase_page()
    {
        Api.Packs = () => ApiResult<IReadOnlyList<PublicPackDto>>.Ok([Starter]);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(NexaVerify.Web.Security.PortalClaims.Portal, NexaVerify.Web.Security.PortalKinds.Client)], "test")),
        };

        var cut = Render<CascadingValue<Microsoft.AspNetCore.Http.HttpContext>>(p => p.Add(x => x.Value, http).AddChildContent<NexaVerify.Web.Pages.Marketing.Pricing>());

        cut.Find("[data-testid=pack-buy]").GetAttribute("href").ShouldBe("/client/billing/buy");
    }

    [Fact]
    public void Plans_without_a_pack_keep_their_contact_us_button_and_the_trial_keeps_its_signup()
    {
        Api.Packs = () => ApiResult<IReadOnlyList<PublicPackDto>>.Ok([Starter]);

        var cut = Render<NexaVerify.Web.Pages.Marketing.Pricing>();

        var plans = cut.FindAll("[data-testid=plan-card]");
        plans.Count.ShouldBe(2);
        plans[0].QuerySelector("a[href='/signup']").ShouldNotBeNull();
        plans[1].QuerySelector("a[href='/contact']").ShouldNotBeNull();
    }

    [Fact]
    public void No_packs_or_a_failed_packs_call_leaves_the_plans_untouched()
    {
        Api.Packs = () => ApiResult<IReadOnlyList<PublicPackDto>>.Ok([]);
        var none = Render<NexaVerify.Web.Pages.Marketing.Pricing>();
        none.FindAll("[data-testid=packs-section]").ShouldBeEmpty();
        none.FindAll("[data-testid=plan-card]").Count.ShouldBe(2);

        Api.Packs = () => ApiResult<IReadOnlyList<PublicPackDto>>.Fail(Problem(500));
        var failed = Render<NexaVerify.Web.Pages.Marketing.Pricing>();
        failed.FindAll("[data-testid=packs-section]").ShouldBeEmpty();
        failed.FindAll("[data-testid=plan-card]").Count.ShouldBe(2);
        failed.FindAll("[data-testid=plans-error]").ShouldBeEmpty();
    }

    [Fact]
    public void Pack_text_from_the_api_is_never_markup()
    {
        Api.Packs = () => ApiResult<IReadOnlyList<PublicPackDto>>.Ok([Starter with { Name = "<img src=x onerror=alert(1)>", Highlights = ["<script>alert(1)</script>"] }]);

        var cut = Render<NexaVerify.Web.Pages.Marketing.Pricing>();

        cut.FindAll("img").ShouldBeEmpty();
        cut.FindAll("script").ShouldBeEmpty();
    }
}
