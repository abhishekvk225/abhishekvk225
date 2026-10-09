using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

/// <summary>Signs a test request in from a header: "client:billing.read,billing.manage" or "admin:...".</summary>
public sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "header-test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var raw) || raw.ToString().Split(':') is not [var portal, var permissions])
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var session = new PortalSession
        {
            Id = "sid-1",
            AccessToken = "never-sent-to-the-browser",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            RefreshToken = "refresh-secret",
            UserId = Guid.NewGuid(),
            Email = "una@acme.test",
            FullName = "Una Admin",
            Portal = portal,
            Roles = ["ClientAdmin"],
            Permissions = permissions.Split(',', StringSplitOptions.RemoveEmptyEntries),
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
        };
        var ticket = new AuthenticationTicket(PortalPrincipalFactory.Create(session), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>The billing relays and the payment-return hop on a small real server, with a scripted API behind the gateway.</summary>
public sealed class BillingBffTests : IAsyncLifetime
{
    private const string InvoiceHtml = "<!doctype html><html><body><h1>Invoice NV-1</h1><script>alert(1)</script></body></html>";
    private readonly BffHarness _bff = new();
    private IHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        await _bff.SignInAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IApiGateway>(_bff.Gateway);
        builder.Services.AddSingleton(new DownloadThrottle(_bff.Clock));
        builder.Services.Configure<SecurityHeadersOptions>(_ => { });
        builder.Services.AddAuthentication(HeaderAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
        builder.Services.AddPortalAuthorization();
        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseBillingReturnHop();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPortalBilling();
        app.MapGet("/client/billing/return", () => "the real page");
        await app.StartAsync();
        _host = app;
        _http = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private static string Invoice(Guid? id = null) => $"/bff/client/billing/orders/{id ?? BillingSample.OrderId}/invoice";

    private HttpRequestMessage Get(string path, string? user = "client:billing.read", string? fetchSite = "same-origin")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        if (fetchSite is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", fetchSite);
        }

        return request;
    }

    private void UpstreamInvoice(string html = InvoiceHtml, string contentType = "text/html") =>
        _bff.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, contentType) };

    // ---- invoice ----

    [Fact]
    public async Task An_invoice_is_relayed_as_its_own_locked_down_page()
    {
        UpstreamInvoice();

        var response = await _http.SendAsync(Get(Invoice()));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBe(InvoiceHtml);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldBe(BillingEndpoints.InvoiceCsp, "the invoice's policy replaces the portal's");
        csp.ShouldStartWith("sandbox;");
        csp.ShouldNotContain("allow-scripts");
        csp.ShouldContain("default-src 'none'");
        csp.ShouldNotContain("script-src");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("Cache-Control").Single().ShouldContain("no-store");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Single().ShouldBe("same-origin");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        var upstream = _bff.Api.Seen.Single();
        upstream.Path.ShouldBe($"/api/v1/client/billing/orders/{BillingSample.OrderId}/invoice");
        upstream.Headers["Accept"].ShouldContain("text/html");
    }

    [Fact]
    public async Task No_token_or_session_detail_reaches_the_browser()
    {
        UpstreamInvoice();

        var response = await _http.SendAsync(Get(Invoice()));
        var everything = string.Join('\n', response.Headers.Select(h => h.Key + ": " + string.Join(',', h.Value))) + await response.Content.ReadAsStringAsync();

        _bff.Api.Seen.Single().Authorization.ShouldStartWith("Bearer ");
        everything.ShouldNotContain("Bearer", Case.Insensitive);
        everything.ShouldNotContain("access-1");
        everything.ShouldNotContain("refresh", Case.Insensitive);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData(null)]
    public async Task Only_the_portals_own_pages_can_open_an_invoice(string? fetchSite)
    {
        UpstreamInvoice();

        var response = await _http.SendAsync(Get(Invoice(), fetchSite: fetchSite));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _bff.Api.Seen.ShouldBeEmpty("nothing was fetched on behalf of another site");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("same-origin")]
    public async Task Same_origin_and_typed_addresses_are_fine(string fetchSite)
    {
        UpstreamInvoice();

        (await _http.SendAsync(Get(Invoice(), fetchSite: fetchSite))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signed_out_people_and_people_without_the_permission_or_from_the_other_portal_get_nothing()
    {
        UpstreamInvoice();

        (await _http.SendAsync(Get(Invoice(), user: null))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _http.SendAsync(Get(Invoice(), user: "client:usage.read"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _http.SendAsync(Get(Invoice(), user: "admin:billing.read"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _bff.Api.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invoices_are_throttled_per_session_and_do_not_use_up_the_csv_allowance()
    {
        UpstreamInvoice();
        for (var i = 0; i < DownloadThrottle.MaxPerWindow; i++)
        {
            (await _http.SendAsync(Get(Invoice()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await _http.SendAsync(Get(Invoice()));

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);
        (await limited.Content.ReadAsStringAsync()).ShouldContain("Please wait a minute");
        _bff.Api.Seen.Count.ShouldBe(DownloadThrottle.MaxPerWindow, "the limited request never reached the API");

        _bff.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("day,count\n", System.Text.Encoding.UTF8, "text/csv") };
        (await _http.SendAsync(Get("/bff/client/billing/orders/export.csv"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        _bff.Clock.Advance(TimeSpan.FromSeconds(61));
        UpstreamInvoice();
        (await _http.SendAsync(Get(Invoice()))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invoice_that_is_not_html_or_far_too_big_is_not_passed_on()
    {
        UpstreamInvoice("{\"hello\":1}", "application/json");
        var wrongType = await _http.SendAsync(Get(Invoice()));
        wrongType.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await wrongType.Content.ReadAsStringAsync()).ShouldNotContain("hello");

        UpstreamInvoice(new string('x', BillingEndpoints.MaxInvoiceBytes + 10));
        var huge = await _http.SendAsync(Get(Invoice()));
        huge.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await huge.Content.ReadAsStringAsync()).Length.ShouldBeLessThan(200);
    }

    [Fact]
    public async Task An_order_that_is_not_yours_is_a_plain_text_not_found_never_html()
    {
        _bff.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.NotFound, "{\"code\":\"NOT_FOUND\",\"correlationId\":\"corr-inv\"}");

        var response = await _http.SendAsync(Get(Invoice()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsStringAsync()).ShouldContain("corr-inv");
    }

    [Fact]
    public async Task A_request_for_something_that_is_not_an_order_id_never_reaches_the_api()
    {
        var response = await _http.SendAsync(Get("/bff/client/billing/orders/..%2F..%2Fadmin/invoice"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _bff.Api.Seen.ShouldBeEmpty();
    }

    // ---- csv ----

    [Fact]
    public async Task The_orders_export_is_relayed_as_a_download_with_the_dates()
    {
        _bff.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("order,total\nNV-1,58.80\n", System.Text.Encoding.UTF8, "text/csv") };

        var response = await _http.SendAsync(Get("/bff/client/billing/orders/export.csv?from=2026-05-01&to=2026-05-31"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldStartWith("nexaverify-orders-");
        (await response.Content.ReadAsStringAsync()).ShouldContain("NV-1,58.80");
        _bff.Api.Seen.Single().Path.ShouldBe("/api/v1/client/billing/orders/export.csv?from=2026-05-01&to=2026-05-31");
    }

    [Theory]
    [InlineData("from=yesterday")]
    [InlineData("from=2026-05-01&to=2026-13-40")]
    [InlineData("to=1;DROP")]
    public async Task Bad_dates_are_refused_before_calling_the_api(string query)
    {
        var response = await _http.SendAsync(Get("/bff/client/billing/orders/export.csv?" + query));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _bff.Api.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_export_is_same_origin_only_throttled_and_needs_the_permission()
    {
        _bff.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a\n", System.Text.Encoding.UTF8, "text/csv") };

        (await _http.SendAsync(Get("/bff/client/billing/orders/export.csv", fetchSite: "cross-site"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _http.SendAsync(Get("/bff/client/billing/orders/export.csv", user: "client:usage.read"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _http.SendAsync(Get("/bff/client/billing/orders/export.csv", user: null))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        for (var i = 0; i < DownloadThrottle.MaxPerWindow; i++)
        {
            (await _http.SendAsync(Get("/bff/client/billing/orders/export.csv"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await _http.SendAsync(Get("/bff/client/billing/orders/export.csv"))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    // ---- coming back from the payment partner ----

    [Fact]
    public async Task A_cross_site_arrival_hops_through_our_own_page_so_the_sign_in_cookie_travels()
    {
        var response = await _http.SendAsync(Get($"/client/billing/return?order={BillingSample.OrderId}&result=success", user: null, fetchSite: "cross-site"));
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain($"url=/client/billing/return?hop=1&amp;order={BillingSample.OrderId}&amp;result=success");
        html.ShouldNotContain("the real page");
        response.Headers.GetValues("Cache-Control").Single().ShouldContain("no-store");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
        _bff.Api.Seen.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("order=%3Cscript%3Ealert(1)%3C%2Fscript%3E&result=javascript:alert(1)")]
    [InlineData("order=not-a-guid&result=%22%3E%3Cimg%20src%3Dx%3E")]
    [InlineData("redirect=https://evil.example&next=//evil.example")]
    public async Task Nothing_but_a_well_formed_order_id_and_a_known_result_is_carried_over(string query)
    {
        var response = await _http.SendAsync(Get("/client/billing/return?" + query, user: null, fetchSite: "cross-site"));
        var html = await response.Content.ReadAsStringAsync();

        html.ShouldContain("url=/client/billing/return?hop=1\"");
        html.ShouldNotContain("evil");
        html.ShouldNotContain("script");
        html.ShouldNotContain("javascript");
        html.ShouldNotContain("<img");
    }

    [Fact]
    public async Task A_visit_from_our_own_site_or_the_address_bar_is_not_hopped()
    {
        foreach (var site in new[] { "same-origin", "none" })
        {
            var response = await _http.SendAsync(Get($"/client/billing/return?order={BillingSample.OrderId}", user: null, fetchSite: site));
            (await response.Content.ReadAsStringAsync()).ShouldBe("the real page");
        }
    }

    [Fact]
    public async Task The_hop_cannot_loop_and_only_covers_the_return_address()
    {
        var again = await _http.SendAsync(Get("/client/billing/return?hop=1&order=" + BillingSample.OrderId, user: null, fetchSite: "cross-site"));
        (await again.Content.ReadAsStringAsync()).ShouldBe("the real page", "a second cross-site arrival with the marker goes straight on");

        var other = await _http.SendAsync(Get("/client/billing/buy", user: null, fetchSite: "cross-site"));
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound, "other addresses are untouched");

        var post = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/client/billing/return") { Headers = { { "Sec-Fetch-Site", "cross-site" } } });
        (await post.Content.ReadAsStringAsync()).ShouldNotContain("refresh");
    }

    [Fact]
    public void The_hop_target_is_rebuilt_not_copied()
    {
        var query = new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["order"] = "00000000-0000-0000-0000-00000000D001",
            ["result"] = "cancelled",
            ["x"] = "y",
        });

        BillingEndpoints.HopTarget(query).ShouldBe("/client/billing/return?hop=1&order=00000000-0000-0000-0000-00000000d001&result=cancelled");
    }
}
