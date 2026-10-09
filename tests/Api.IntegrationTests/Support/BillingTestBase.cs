using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests.Support;

/// <summary>Shared set-up for the payment tests: a real API on a fresh database, a Super Admin, and helpers for packs, profiles, checkouts and orders.</summary>
public abstract class BillingTestBase : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;

    protected BillingTestBase(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected AuthApp App { get; private set; } = null!;

    protected LoginResponse Platform { get; private set; } = null!;

    protected virtual Dictionary<string, string> Settings => BillingSettings("Simulated");

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public static Dictionary<string, string> BillingSettings(string provider) => new()
    {
        ["Billing:Enabled"] = "true",
        ["Billing:Provider"] = provider,
        ["Billing:TaxPercent"] = "18",
        ["Billing:TaxLabel"] = "GST",
        ["Billing:MaxCheckoutsPerUserPerHour"] = "1000",
        ["Billing:MaxCheckoutsPerClientPerHour"] = "1000",
        ["Billing:MaxOpenOrdersPerClient"] = "100",
        ["Billing:Seller:LegalName"] = "Seller Pvt Ltd",
        ["Billing:Seller:AddressLines:0"] = "1 Seller Street",
        ["Billing:Seller:AddressLines:1"] = "Mumbai 400001",
        ["Billing:Seller:TaxId"] = "27AAAAA0000A1Z5",
        ["Billing:Seller:TaxIdLabel"] = "GSTIN",
        ["Billing:Stripe:SecretKey"] = "sk_test_integration",
        ["Billing:Stripe:WebhookSecret"] = StripeWebhookSecret,
        ["Billing:Razorpay:KeyId"] = "rzp_test_integration",
        ["Billing:Razorpay:KeySecret"] = "rzp_secret_integration",
        ["Billing:Razorpay:WebhookSecret"] = "rzp_webhook_integration",
        ["Webhooks:AllowUnsafeTargets"] = "true",
        ["Webhooks:BackgroundEnabled"] = "false",
    };

    public const string StripeWebhookSecret = "whsec_integration_secret";

    public virtual async Task InitializeAsync()
    {
        App = await AuthApp.CreateAsync(_fixture, Settings, ConfigureServices);
        Platform = await App.SuperAdminAsync();
    }

    public virtual async Task DisposeAsync() => await App.DisposeAsync();

    protected sealed record Tenant(Guid ClientId, string Token, string Email);

    protected async Task<Tenant> NewTenantAsync(string code)
    {
        var email = $"admin@{code.ToLowerInvariant()}.test";
        var (client, admin) = await App.OnboardClientAsync(Platform.AccessToken, code, email);
        return new Tenant(client.Id, admin.AccessToken, email);
    }

    protected async Task<string> NewClientUserTokenAsync(Guid clientId, string email, string role = "ClientUser")
    {
        await App.CreateClientUserAsync(clientId, email, role);
        return (await App.LoginAsync(email, AuthApp.StrongPassword)).AccessToken;
    }

    protected static UpdateBillingProfileRequest Profile(string email = "billing@acme.test") =>
        new("Acme Pvt Ltd", "1 MG Road", null, "Bengaluru", "Karnataka", "560001", "IN", "29ABCDE1234F1Z5", email);

    protected async Task SaveProfileAsync(Tenant tenant, UpdateBillingProfileRequest? profile = null)
    {
        var response = await App.PutAsync("/api/v1/client/billing/profile", profile ?? Profile(), tenant.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    protected async Task<AdminCreditPackDto> NewPackAsync(
        string name = "Growth", int credits = 1_000, int days = 90, long priceMinor = 100_000, string currency = "INR", bool isPublic = true, bool isActive = true, int order = 0)
    {
        var response = await App.PostAsync(
            "/api/v1/admin/billing/packs",
            new CreateCreditPackRequest(name, "A pack", credits, days, priceMinor, currency, ["Highlight one", "Highlight two"], order, isActive, isPublic),
            Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdminCreditPackDto>(AuthApp.Json))!;
    }

    protected Task<HttpResponseMessage> CheckoutAsync(Tenant tenant, Guid packId, string? key = null) =>
        App.PostAsync("/api/v1/client/billing/checkout", new CheckoutRequest(packId, key), tenant.Token);

    protected async Task<CheckoutResponseDto> StartCheckoutAsync(Tenant tenant, Guid packId, string? key = null)
    {
        var response = await CheckoutAsync(tenant, packId, key);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>(AuthApp.Json))!;
    }

    protected async Task<OrderDto> GetOrderAsync(Tenant tenant, Guid orderId)
    {
        var response = await App.GetAsync($"/api/v1/client/billing/orders/{orderId}", tenant.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderDto>(AuthApp.Json))!;
    }

    protected Task<HttpResponseMessage> SimulateAsync(Tenant tenant, Guid orderId, string outcome) =>
        App.PostAsync($"/api/v1/dev/billing/simulate/{orderId}", new SimulatePaymentRequest(outcome), tenant.Token);

    protected async Task<OrderDto> PayAsync(Tenant tenant, Guid orderId)
    {
        var response = await SimulateAsync(tenant, orderId, "success");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OrderDto>(AuthApp.Json))!;
    }

    /// <summary>Profile + pack + checkout + payment in one go; returns the paid order.</summary>
    protected async Task<OrderDto> BuyAsync(Tenant tenant, AdminCreditPackDto pack)
    {
        var checkout = await StartCheckoutAsync(tenant, pack.Id);
        return await PayAsync(tenant, checkout.OrderId);
    }

    protected async Task<AdminOrderDto> AdminOrderAsync(Guid orderId)
    {
        var response = await App.GetAsync($"/api/v1/admin/billing/orders/{orderId}", Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdminOrderDto>(AuthApp.Json))!;
    }

    protected Task<List<LicenseTransaction>> LedgerAsync(Guid clientId, Guid licenseId) =>
        App.WithTenantDbAsync(clientId, db => db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == licenseId).OrderBy(t => t.Id).ToListAsync());

    protected Task<License> LicenseAsync(Guid clientId, Guid licenseId) =>
        App.WithTenantDbAsync(clientId, db => db.Licenses.AsNoTracking().SingleAsync(l => l.Id == licenseId));

    protected Task<List<string>> AuditActionsAsync(Guid clientId) =>
        App.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ClientId == clientId).OrderBy(a => a.OccurredAt).Select(a => a.Action).ToListAsync());

    protected Task<List<WebhookDelivery>> DeliveriesAsync(Guid clientId) =>
        App.WithTenantDbAsync(clientId, db => db.WebhookDeliveries.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    protected Task<List<PaymentEvent>> EventsAsync() =>
        App.WithDbAsync(db => db.PaymentEvents.AsNoTracking().OrderBy(e => e.ReceivedAt).ToListAsync());

    protected Task<int> LicenseCountAsync(Guid clientId, string namePrefix) =>
        App.WithTenantDbAsync(clientId, db => db.Licenses.CountAsync(l => l.Name.StartsWith(namePrefix)));

    protected Task SetConsumedAsync(Guid licenseId, int consumed) =>
        App.WithDbAsync(async db =>
        {
            await db.Licenses.Where(l => l.Id == licenseId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ConsumedCredits, consumed));
            return true;
        });

    protected Task AgeOrderAsync(Guid orderId, TimeSpan age) =>
        App.WithDbAsync(async db =>
        {
            var created = DateTime.UtcNow - age;
            await db.PaymentOrders.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedAt, created));
            return true;
        });

    protected async Task<LicenseDto> ClientLicenseAsync(Tenant tenant, Guid licenseId)
    {
        var response = await App.GetAsync($"/api/v1/client/licenses/{licenseId}", tenant.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LicenseDto>(AuthApp.Json))!;
    }

    protected static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return json.GetProperty("code").GetString()!;
    }
}
