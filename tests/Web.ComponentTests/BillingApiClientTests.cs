using System.Net;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Web.ComponentTests;

public class BillingApiClientTests
{
    private static async Task<(BffHarness Harness, BillingApiClient Client)> NewAsync()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        return (h, new BillingApiClient(h.Gateway));
    }

    private const string OrderJson = "{\"id\":\"00000000-0000-0000-0000-00000000d001\",\"invoiceNumber\":\"NV-1\",\"packName\":\"Starter\",\"credits\":5000,\"validityDays\":365,\"subtotalMinor\":4900,\"taxMinor\":980,\"totalMinor\":5880,\"taxPercent\":20,\"taxLabel\":\"VAT\",\"currency\":\"USD\",\"status\":\"Paid\",\"createdAt\":\"2026-06-15T09:00:00Z\",\"paidAt\":\"2026-06-15T09:01:00Z\",\"licenseId\":\"00000000-0000-0000-0000-00000000f001\"}";

    [Fact]
    public async Task Packs_are_read_from_the_client_route_with_the_servers_amounts()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "[{\"id\":\"00000000-0000-0000-0000-0000000000b1\",\"name\":\"Starter\",\"description\":null,\"credits\":5000,\"validityDays\":365,\"priceMinor\":4900,\"currency\":\"USD\",\"taxPercent\":20,\"taxLabel\":\"VAT\",\"totalMinor\":5880,\"highlights\":[\"a\"]}]");

        var result = await client.GetPacksAsync();

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var pack = result.Value.Single();
        (pack.PriceMinor, pack.TotalMinor, pack.TaxPercent, pack.TaxLabel).ShouldBe((4900L, 5880L, 20m, "VAT"));
        var seen = h.Api.Seen.Single();
        seen.Method.ShouldBe(HttpMethod.Get);
        seen.Path.ShouldBe("/api/v1/client/billing/packs");
        seen.Authorization.ShouldStartWith("Bearer ", Case.Sensitive, "the token is added on the server");
    }

    [Fact]
    public async Task Checkout_sends_only_the_pack_and_the_key_never_an_amount()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.Created, "{\"orderId\":\"00000000-0000-0000-0000-00000000d001\",\"checkoutUrl\":\"https://pay.example.test/s/1\",\"expiresAt\":\"2026-06-15T09:30:00Z\"}");

        var result = await client.CheckoutAsync(BillingSample.PackStarter, "key-1");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.CheckoutUrl.ShouldBe("https://pay.example.test/s/1");
        var seen = h.Api.Seen.Single();
        seen.Method.ShouldBe(HttpMethod.Post);
        seen.Path.ShouldBe("/api/v1/client/billing/checkout");
        seen.Headers["Idempotency-Key"].ShouldBe("key-1");
        using var body = System.Text.Json.JsonDocument.Parse(seen.Body!);
        body.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["idempotencyKey", "packId"]);
        body.RootElement.GetProperty("idempotencyKey").GetString().ShouldBe("key-1");
        seen.Body!.ShouldNotContain("price", Case.Insensitive);
        seen.Body!.ShouldNotContain("total", Case.Insensitive);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Add your company address first.", "BILLING_PROFILE_INCOMPLETE")]
    [InlineData(HttpStatusCode.Conflict, "You already have a payment in progress.", "ORDER_IN_PROGRESS")]
    public async Task Checkout_refusals_keep_their_plain_message_and_field_errors(HttpStatusCode status, string detail, string code)
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(status, $"{{\"code\":\"{code}\",\"detail\":\"{detail}\",\"errors\":{{\"legalName\":[\"Enter your company name.\"]}},\"correlationId\":\"corr-pay\"}}");

        var result = await client.CheckoutAsync(BillingSample.PackStarter, "k");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(code);
        result.Error.Message.ShouldBe(detail);
        result.Error.CorrelationId.ShouldBe("corr-pay");
        result.Error.FieldErrors!["legalName"].ShouldBe(["Enter your company name."]);
    }

    [Fact]
    public async Task Orders_are_listed_with_paging_and_the_status_filter()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "{\"items\":[{\"id\":\"00000000-0000-0000-0000-00000000d001\",\"invoiceNumber\":null,\"packName\":\"Starter\",\"credits\":5000,\"totalMinor\":5880,\"currency\":\"USD\",\"status\":\"PartiallyRefunded\",\"createdAt\":\"2026-06-15T09:00:00Z\",\"paidAt\":null}],\"page\":2,\"pageSize\":10,\"totalCount\":11,\"totalPages\":2}");

        var result = await client.ListOrdersAsync(new PageRequest { Page = 2, PageSize = 10 }, "Paid");

        result.Value.Items.Single().Status.ShouldBe("PartiallyRefunded");
        result.Value.TotalCount.ShouldBe(11);
        h.Api.Seen.Single().Path.ShouldBe("/api/v1/client/billing/orders?page=2&pageSize=10&status=Paid");
    }

    [Fact]
    public async Task One_order_is_read_and_a_poll_is_marked_passive()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, OrderJson);
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        var seenBefore = (await h.Store.GetAsync("sid-1"))!.LastSeenAt;

        var polled = await client.GetOrderAsync(BillingSample.OrderId, default, passive: true);

        polled.Value.Status.ShouldBe("Paid");
        polled.Value.LicenseId.ShouldNotBeNull();
        h.Api.Seen.Single().Path.ShouldBe($"/api/v1/client/billing/orders/{BillingSample.OrderId}");
        (await h.Store.GetAsync("sid-1"))!.LastSeenAt.ShouldBe(seenBefore, "waiting for a payment is not activity that keeps the session alive");

        await client.GetOrderAsync(BillingSample.OrderId);
        (await h.Store.GetAsync("sid-1"))!.LastSeenAt.ShouldBe(h.Clock.GetUtcNow());
    }

    [Fact]
    public async Task A_missing_billing_profile_reads_as_an_empty_one_but_other_errors_stay_errors()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.NotFound, "{\"code\":\"NOT_FOUND\"}");
        var empty = await client.GetProfileAsync();
        empty.IsSuccess.ShouldBeTrue();
        BillingProfileForm.LooksIncomplete(empty.Value).ShouldBeTrue();

        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.ServiceUnavailable, "{}", "corr-503");
        var failed = await client.GetProfileAsync();
        failed.IsSuccess.ShouldBeFalse();
        failed.Error!.CorrelationId.ShouldBe("corr-503");
    }

    [Fact]
    public async Task Saving_the_profile_puts_the_details_then_reads_them_back_with_the_new_version()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = r => r.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : ScriptedApi.Json(HttpStatusCode.OK, "{\"legalName\":\"Acme\",\"addressLine1\":\"1 High St\",\"city\":\"London\",\"postalCode\":\"EC1\",\"country\":\"GB\",\"billingEmail\":\"b@acme.test\",\"rowVersion\":\"NEWVER\"}");

        var result = await client.SaveProfileAsync(new SaveBillingProfileRequest("Acme", "1 High St", null, "London", null, "EC1", "GB", "GB123", "b@acme.test", "OLDVER"));

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.RowVersion.ShouldBe("NEWVER");
        h.Api.Seen.Select(s => (s.Method.Method, s.Path)).ShouldBe([("PUT", "/api/v1/client/billing/profile"), ("GET", "/api/v1/client/billing/profile")]);
        h.Api.Seen[0].Body!.ShouldContain("\"rowVersion\":\"OLDVER\"");
        h.Api.Seen[0].Body!.ShouldContain("\"taxId\":\"GB123\"");
    }

    [Fact]
    public async Task A_refused_profile_save_does_not_read_back_and_returns_the_field_errors()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.UnprocessableEntity, "{\"code\":\"VALIDATION_FAILED\",\"errors\":{\"taxId\":[\"That tax number is not valid.\"]}}");

        var result = await client.SaveProfileAsync(new SaveBillingProfileRequest("A", "B", null, "C", null, "D", "GB", "bad", "e@f.test", null));

        result.Error!.FieldErrors!["taxId"].ShouldBe(["That tax number is not valid."]);
        h.Api.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_simulator_posts_the_outcome_to_the_dev_route()
    {
        var (h, _) = await NewAsync();
        h.Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await new DevBillingApiClient(h.Gateway).SimulateAsync(BillingSample.OrderId, "success");

        result.IsSuccess.ShouldBeTrue();
        var seen = h.Api.Seen.Single();
        seen.Path.ShouldBe($"/api/v1/dev/billing/simulate/{BillingSample.OrderId}");
        seen.Body.ShouldBe("{\"outcome\":\"success\"}");
    }

    [Fact]
    public async Task Staff_calls_use_the_admin_routes_and_filters()
    {
        var (h, _) = await NewAsync();
        var admin = new AdminBillingApiClient(h.Gateway);
        var clientId = Guid.Parse("00000000-0000-0000-0000-00000000c001");
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"items\":[],\"page\":1,\"pageSize\":25,\"totalCount\":0}");

        await admin.ListOrdersAsync(new AdminOrderQuery { ClientId = clientId, Status = "Paid", From = new DateOnly(2026, 6, 1), To = new DateOnly(2026, 6, 30), Page = 3, PageSize = 25 });

        h.Api.Seen.Single().Path.ShouldBe($"/api/v1/admin/billing/orders?clientId={clientId}&status=Paid&from=2026-06-01&to=2026-06-30&page=3&pageSize=25");

        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"order\":{\"id\":\"00000000-0000-0000-0000-00000000d001\",\"invoiceNumber\":\"NV-1\",\"packName\":\"S\",\"credits\":1,\"validityDays\":1,\"subtotalMinor\":1,\"taxMinor\":0,\"totalMinor\":1,\"taxPercent\":0,\"taxLabel\":null,\"currency\":\"USD\",\"status\":\"Paid\",\"createdAt\":\"2026-06-15T09:00:00Z\",\"paidAt\":null,\"licenseId\":null},\"clientId\":\"00000000-0000-0000-0000-00000000c001\",\"clientName\":\"Acme\",\"provider\":\"stripe\",\"providerSessionId\":null,\"providerPaymentId\":\"pi_1\",\"creditsRevoked\":1,\"failureReason\":null,\"refunds\":[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"amountMinor\":1,\"creditsRevoked\":1,\"reason\":\"dup\",\"status\":\"Succeeded\",\"providerRefundId\":null,\"createdBy\":null,\"createdAt\":\"2026-06-16T09:00:00Z\"}]}");
        var order = await admin.RefundAsync(BillingSample.OrderId, new RefundOrderRequest(1, "dup"));
        await admin.ReconcileAsync(BillingSample.OrderId);

        order.Value.Refunds!.Single().Reason.ShouldBe("dup");
        order.Value.ClientName.ShouldBe("Acme");
        order.Value.PackName.ShouldBe("S");
        order.Value.Provider.ShouldBe("stripe");
        order.Value.Status.ShouldBe("Paid");
        h.Api.Seen[1].Path.ShouldBe($"/api/v1/admin/billing/orders/{BillingSample.OrderId}/refund");
        h.Api.Seen[1].Body.ShouldBe("{\"amountMinor\":1,\"reason\":\"dup\"}");
        h.Api.Seen[2].Path.ShouldBe($"/api/v1/admin/billing/orders/{BillingSample.OrderId}/reconcile");
        h.Api.Seen[2].Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task Packs_are_created_and_updated_with_minor_units_and_a_row_version()
    {
        var (h, _) = await NewAsync();
        var admin = new AdminBillingApiClient(h.Gateway);
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"id\":\"00000000-0000-0000-0000-0000000000b1\",\"name\":\"S\",\"credits\":1,\"validityDays\":1,\"priceMinor\":1500,\"currency\":\"JPY\",\"displayOrder\":0,\"isActive\":true,\"isPublic\":true}");

        await admin.CreatePackAsync(new SaveAdminPackRequest("S", null, 1, 1, 1500, "JPY", ["a"], 0, true, true, null));
        await admin.UpdatePackAsync(BillingSample.PackStarter, new SaveAdminPackRequest("S", null, 1, 1, 1500, "JPY", [], 0, false, true, "RV"));

        h.Api.Seen[0].Method.ShouldBe(HttpMethod.Post);
        h.Api.Seen[0].Path.ShouldBe("/api/v1/admin/billing/packs");
        h.Api.Seen[0].Body!.ShouldContain("\"priceMinor\":1500");
        h.Api.Seen[1].Method.ShouldBe(HttpMethod.Put);
        h.Api.Seen[1].Body!.ShouldContain("\"rowVersion\":\"RV\"");
    }

    [Fact]
    public async Task The_public_packs_call_sends_no_credentials_and_is_cached_by_the_decorator()
    {
        var h = new BffHarness();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "[{\"id\":\"00000000-0000-0000-0000-0000000000b1\",\"name\":\"Starter\",\"description\":\"d\",\"credits\":5000,\"validityDays\":365,\"priceMinor\":4900,\"currency\":\"USD\",\"displayPrice\":\"$49\",\"highlights\":[\"a\"]}]");
        var inner = new PublicApiClient(h.Gateway);
        var cached = new CachingPublicApiClient(inner, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        var first = await cached.GetPacksAsync();
        await cached.GetPacksAsync();

        first.Value.Single().PriceMinor.ShouldBe(4900);
        h.Api.Seen.Count.ShouldBe(1, "second call came from the cache");
        h.Api.Seen[0].Path.ShouldBe("/api/v1/public/packs");
        h.Api.Seen[0].Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_public_packs_call_is_not_cached()
    {
        var h = new BffHarness();
        var status = HttpStatusCode.InternalServerError;
        h.Api.Respond = _ => ScriptedApi.Json(status, status == HttpStatusCode.OK ? "[]" : "{}");
        var cached = new CachingPublicApiClient(new PublicApiClient(h.Gateway), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        (await cached.GetPacksAsync()).IsSuccess.ShouldBeFalse();
        status = HttpStatusCode.OK;
        (await cached.GetPacksAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_gateway_only_asks_for_known_media_types()
    {
        var h = new BffHarness();
        await h.SignInAsync();

        await Should.ThrowAsync<ArgumentException>(() => h.Gateway.OpenStreamAsync("x", default, new ApiCallOptions { Accept = "application/x-evil" }));
    }
}
