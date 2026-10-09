using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Billing;
using NexaVerify.Application.Public;

namespace NexaVerify.Infrastructure.Billing;

/// <summary>
/// A stand-in payment provider for local development and automated tests: its "checkout page" is a page of the portal, and the outcome
/// is chosen with <c>POST /api/v1/dev/billing/simulate/{orderId}</c>. It can not be constructed outside the Development and Testing
/// environments (the host also refuses to start), and it accepts no webhooks at all.
/// </summary>
public sealed class SimulatedProvider : IPaymentProvider
{
    public const string ProviderName = "simulated";

    private readonly PortalLinksOptions _portal;

    public SimulatedProvider(IHostEnvironment environment, IOptions<PortalLinksOptions> portal)
    {
        if (!IsAllowedIn(environment))
        {
            throw new InvalidOperationException("Billing:Provider=Simulated is only permitted in the Development and Testing environments.");
        }

        _portal = portal.Value;
    }

    public string Name => ProviderName;

    public static bool IsAllowedIn(IHostEnvironment environment) => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutOrder order, Uri successUrl, Uri cancelUrl, CancellationToken cancellationToken) =>
        Task.FromResult(new CheckoutSession("sim_" + order.OrderId.ToString("N"), _portal.LinkTo($"dev/pay/{order.OrderId:D}"), order.ExpiresAt));

    public WebhookParseResult VerifyAndParseWebhook(IReadOnlyDictionary<string, string> headers, byte[] rawBody) => WebhookParseResult.Invalid;

    public Task<ProviderRefund> RefundAsync(
        string providerPaymentId, long amountMinor, string currency, string reason, string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderRefund("sim_re_" + idempotencyKey));

    public Task<ProviderPaymentState> FetchPaymentAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderPaymentState(ProviderPaymentStatus.Pending, null, null, null));

    public Task CancelCheckoutAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Resolves the provider named by <c>Billing:Provider</c>. A webhook route naming any other provider finds nothing (404).</summary>
public sealed class PaymentProviderResolver : IPaymentProviderResolver
{
    private readonly IServiceProvider _services;
    private readonly BillingOptions _options;

    public PaymentProviderResolver(IServiceProvider services, IOptions<BillingOptions> options)
    {
        _services = services;
        _options = options.Value;
    }

    public IPaymentProvider? Current => _options.Enabled ? Find(_options.Provider) : null;

    public IPaymentProvider? Find(string name)
    {
        if (!_options.Enabled || !string.Equals(name, _options.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name.ToLowerInvariant() switch
        {
            StripeProvider.ProviderName => _services.GetRequiredService<StripeProvider>(),
            RazorpayProvider.ProviderName => _services.GetRequiredService<RazorpayProvider>(),
            SimulatedProvider.ProviderName => _services.GetRequiredService<SimulatedProvider>(),
            _ => null,
        };
    }
}
