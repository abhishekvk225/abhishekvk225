using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Dashboards;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Common;

namespace NexaVerify.Application.Billing;

public interface IBillingService
{
    BillingConfigDto GetConfig();

    Task<Result<IReadOnlyList<CreditPackDto>>> ListPacksAsync(CancellationToken cancellationToken);

    Task<Result<CheckoutResponseDto>> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);

    Task<Result<PagedResult<OrderListItemDto>>> ListOrdersAsync(OrderListQuery query, CancellationToken cancellationToken);

    /// <summary>One order. A Pending order whose webhook is late is confirmed with the provider first (at most once per <c>Billing:InlineReconcileSeconds</c>).</summary>
    Task<Result<OrderDto>> GetOrderAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<OrderDto>> CancelOrderAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<InvoiceDocument>> GetInvoiceAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<UsageReportStream>> ExportOrdersAsync(CancellationToken cancellationToken);

    Task<Result<BillingProfileDto>> GetProfileAsync(CancellationToken cancellationToken);

    Task<Result<BillingProfileDto>> UpdateProfileAsync(UpdateBillingProfileRequest request, CancellationToken cancellationToken);

    /// <summary>Development and test hosts: plays the part of the payment provider for one of the caller's own orders.</summary>
    Task<Result<OrderDto>> SimulateAsync(Guid id, SimulatePaymentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The signed-in client's side of billing. The tenant always comes from the credential; the amount of an order always comes from the
/// pack in the database (the request carries a pack id and nothing else). A redirect back from the provider proves nothing: only a
/// verified webhook, or the provider's own answer to our server-side query, can mark an order paid.
/// </summary>
public sealed partial class BillingService : IBillingService
{
    private const int ExportRowCap = 10_000;

    private readonly ICreditPackRepository _packs;
    private readonly IPaymentOrderRepository _orders;
    private readonly IBillingProfileRepository _profiles;
    private readonly IPaymentOrderAtomics _atomics;
    private readonly IPaymentProviderResolver _providers;
    private readonly IPaymentReconciler _reconciler;
    private readonly IPaymentEventProcessor _processor;
    private readonly IBillingThrottle _throttle;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly BillingOptions _options;
    private readonly PortalLinksOptions _portal;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        ICreditPackRepository packs,
        IPaymentOrderRepository orders,
        IBillingProfileRepository profiles,
        IPaymentOrderAtomics atomics,
        IPaymentProviderResolver providers,
        IPaymentReconciler reconciler,
        IPaymentEventProcessor processor,
        IBillingThrottle throttle,
        ICurrentUser currentUser,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        IOptions<BillingOptions> options,
        IOptions<PortalLinksOptions> portal,
        TimeProvider time,
        ILogger<BillingService> logger)
    {
        _packs = packs;
        _orders = orders;
        _profiles = profiles;
        _atomics = atomics;
        _providers = providers;
        _reconciler = reconciler;
        _processor = processor;
        _throttle = throttle;
        _currentUser = currentUser;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _portal = portal.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public BillingConfigDto GetConfig() => new(
        _options.Enabled,
        _options.TaxPercent,
        _options.TaxLabel,
        _options.AllowedCurrencies.Select(c => c.ToUpperInvariant()).Distinct().ToList(),
        _options.RequireBillingProfile,
        _options.RequireTaxId);

    public async Task<Result<IReadOnlyList<CreditPackDto>>> ListPacksAsync(CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        var packs = await _packs.ListAsync(activeOnly: true, publicOnly: false, cancellationToken);
        return Result<IReadOnlyList<CreditPackDto>>.Success(packs.Where(p => _options.AllowsCurrency(p.Currency)).Select(p => p.ToClientDto(_options)).ToList());
    }

    public async Task<Result<CheckoutResponseDto>> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        if (_currentUser.ClientId is not { } clientId || _currentUser.ActorId is not { } userId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only signed-in client users can buy credits.");
        }

        var provider = _providers.Current;
        if (provider is null)
        {
            return Error.Unavailable(ErrorCodes.PaymentProviderUnavailable, "Online payments are not available right now.");
        }

        if (!await _throttle.TryAcquireCheckoutAsync(userId, clientId, cancellationToken))
        {
            return Error.TooManyRequests(ErrorCodes.RateLimited, "Too many checkouts. Try again later.");
        }

        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (key is not null && await _orders.FindByIdempotencyKeyAsync(key, cancellationToken) is { } existing)
        {
            return Replay(existing, request.PackId);
        }

        var pack = await _packs.GetAsync(request.PackId, cancellationToken);
        if (pack is null || !pack.IsActive || !_options.AllowsCurrency(pack.Currency))
        {
            return Error.NotFound("The credit pack was not found.");
        }

        var profile = await _profiles.GetAsync(cancellationToken);
        if (_options.RequireBillingProfile && (profile is null || !profile.IsComplete(_options.RequireTaxId)))
        {
            return Error.Conflict(ErrorCodes.BillingProfileIncomplete, "Complete your billing details before buying credits.");
        }

        var now = Now;
        if (await _orders.CountOpenAsync(now, cancellationToken) >= _options.MaxOpenOrdersPerClient)
        {
            return Error.TooManyRequests(ErrorCodes.RateLimited, "You have too many unfinished payments. Complete or cancel one first.");
        }

        var buyer = BuyerSnapshot.From(profile);
        PaymentOrder order;
        try
        {
            order = PaymentOrder.Create(clientId, pack, _options.TaxPercent, _options.TaxLabel, provider.Name, key, buyer.ToJson(), now.AddMinutes(_options.CheckoutExpiryMinutes));
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _orders.Add(order);
        _audit.Record(new AuditEntry("billing.checkout_created", nameof(PaymentOrder), order.Id.ToString(), clientId,
            NewValues: new { OrderId = order.Id, PackId = pack.Id, pack.Name, order.Credits, order.TotalMinor, order.Currency, order.Provider }));
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException) when (key is not null)
        {
            // Two requests with one idempotency key raced: the other one created the order.
            _unitOfWork.ClearTracked();
            return await _orders.FindByIdempotencyKeyAsync(key, cancellationToken) is { } winner
                ? Replay(winner, request.PackId)
                : Error.Conflict(ErrorCodes.Conflict, "The checkout could not be created. Try again.");
        }

        var successUrl = ReturnUrl(_options.SuccessPath, order.Id);
        var cancelUrl = ReturnUrl(_options.CancelPath, order.Id);
        try
        {
            var session = await provider.CreateCheckoutAsync(
                new CheckoutOrder(
                    order.Id, order.PackName, order.Credits, order.SubtotalMinor, order.TaxMinor, order.TotalMinor, order.Currency, order.TaxPercent, order.TaxLabel,
                    string.IsNullOrWhiteSpace(buyer.LegalName) ? null : buyer.LegalName, string.IsNullOrWhiteSpace(buyer.BillingEmail) ? null : buyer.BillingEmail, order.ExpiresAt),
                successUrl,
                cancelUrl,
                cancellationToken);
            order.AttachCheckout(session.SessionId, session.CheckoutUrl, session.ExpiresAt);
            await _unitOfWork.SaveChangesAsync(CancellationToken.None); // the provider already has the session: always record it
            LogCheckoutCreated(order.Id, clientId, order.Provider);
            return new CheckoutResponseDto(order.Id, session.CheckoutUrl, session.ExpiresAt);
        }
        catch (PaymentProviderException ex)
        {
            LogCheckoutFailed(order.Id, order.Provider, ex.Message);
            order.FailCheckout("The payment provider could not open a payment page.");
            await _unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Error.Unavailable(ErrorCodes.PaymentProviderUnavailable, "The payment provider is not available right now. Try again in a moment.");
        }
    }

    public async Task<Result<PagedResult<OrderListItemDto>>> ListOrdersAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        if (!TryStatus(query.Status, out var status))
        {
            return Error.Validation("Unknown status filter.");
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _orders.ListAsync(null, status, null, null, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<OrderListItemDto>(items.Select(r => r.Order.ToListItem()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<OrderDto>> GetOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        var order = await _orders.GetNoTrackingAsync(id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("The order was not found.");
        }

        if (order.Status == PaymentOrderStatus.Pending && order.ProviderSessionId is not null && _options.InlineReconcileSeconds > 0
            && await _atomics.TryClaimCheckAsync(order.Id, Now, TimeSpan.FromSeconds(_options.InlineReconcileSeconds), cancellationToken))
        {
            // The webhook may simply be late. Ask the provider; if it is unreachable the page still answers with what we know.
            await _reconciler.ReconcileAsync(order.Id, cancellationToken);
            order = await _orders.GetNoTrackingAsync(id, cancellationToken) ?? order;
        }

        return order.ToDto();
    }

    public async Task<Result<OrderDto>> CancelOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        var order = await _orders.GetNoTrackingAsync(id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("The order was not found.");
        }

        var cancelled = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (!await _atomics.TryTransitionAsync(order.Id, [PaymentOrderStatus.Pending], PaymentOrderStatus.Cancelled, "Cancelled by the customer", Now, ct))
                {
                    return false;
                }

                _audit.Record(new AuditEntry("billing.order_cancelled", nameof(PaymentOrder), order.Id.ToString(), order.ClientId));
                await _unitOfWork.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);
        if (!cancelled)
        {
            return Error.Conflict(ErrorCodes.Conflict, "Only an unpaid order can be cancelled.");
        }

        if (order.ProviderSessionId is not null && _providers.Find(order.Provider) is { } provider)
        {
            try
            {
                await provider.CancelCheckoutAsync(order.ProviderSessionId, cancellationToken);
            }
            catch (PaymentProviderException ex)
            {
                LogCheckoutFailed(order.Id, order.Provider, ex.Message); // best effort: a late payment will surface as an anomaly
            }
        }

        return (await _orders.GetNoTrackingAsync(id, cancellationToken))!.ToDto();
    }

    public async Task<Result<InvoiceDocument>> GetInvoiceAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        var row = await _orders.GetRowAsync(id, cancellationToken);
        if (row is null)
        {
            return Error.NotFound("The order was not found.");
        }

        if (row.Order.InvoiceNumber is null)
        {
            return Error.Conflict(ErrorCodes.Conflict, "An invoice exists once the order is paid.");
        }

        var refunds = await _orders.GetRefundsAsync(id, cancellationToken);
        return InvoiceRenderer.Render(row.Order, row.ClientName, refunds, _options.Seller);
    }

    public async Task<Result<UsageReportStream>> ExportOrdersAsync(CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        var (items, _) = await _orders.ListAsync(null, null, null, null, 0, ExportRowCap, cancellationToken);
        _audit.Record(new AuditEntry("report.exported", "BillingOrders", "client-orders", _currentUser.ClientId, NewValues: new { Report = "client-orders", Rows = items.Count }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new UsageReportStream("orders.csv", OrderLines(items));
    }

    public async Task<Result<BillingProfileDto>> GetProfileAsync(CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        return (await _profiles.GetAsync(cancellationToken)).ToDto(_options.RequireTaxId);
    }

    public async Task<Result<BillingProfileDto>> UpdateProfileAsync(UpdateBillingProfileRequest request, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        if (_currentUser.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client accounts have a billing profile.");
        }

        var profile = await _profiles.GetAsync(cancellationToken);
        try
        {
            if (profile is null)
            {
                profile = BillingProfile.Create(clientId, request.LegalName, request.AddressLine1, request.AddressLine2, request.City, request.State, request.PostalCode, request.Country, request.TaxId, request.BillingEmail);
                _profiles.Add(profile);
            }
            else
            {
                profile.Update(request.LegalName, request.AddressLine1, request.AddressLine2, request.City, request.State, request.PostalCode, request.Country, request.TaxId, request.BillingEmail);
            }
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _audit.Record(new AuditEntry("billing.profile_updated", nameof(BillingProfile), profile.Id.ToString(), clientId,
            NewValues: new { profile.LegalName, profile.City, profile.Country, HasTaxId = profile.TaxId is not null }));
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            _unitOfWork.ClearTracked(); // a parallel first save created it: ask the client to retry rather than overwrite blindly
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "The billing profile was changed at the same time. Reload and try again.");
        }

        return profile.ToDto(_options.RequireTaxId);
    }

    public async Task<Result<OrderDto>> SimulateAsync(Guid id, SimulatePaymentRequest request, CancellationToken cancellationToken)
    {
        if (Guard() is { } denied)
        {
            return denied;
        }

        if (!_options.IsProvider(PaymentProviders.Simulated))
        {
            return Error.NotFound();
        }

        var order = await _orders.GetNoTrackingAsync(id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("The order was not found.");
        }

        var type = request.Outcome.Trim().ToLowerInvariant() switch
        {
            "success" => PaymentEventType.PaymentSucceeded,
            "failure" => PaymentEventType.PaymentFailed,
            "expired" => PaymentEventType.CheckoutExpired,
            _ => (PaymentEventType?)null,
        };
        if (type is null)
        {
            return Error.Validation("The outcome must be success, failure or expired.", new Dictionary<string, string[]> { ["outcome"] = ["Use success, failure or expired."] });
        }

        var evt = new NormalisedPaymentEvent(
            "simulated", "sim-" + Guid.CreateVersion7().ToString("N"), type.Value, order.Id,
            "sim_pay_" + order.Id.ToString("N"), order.TotalMinor, order.Currency);
        var processed = await _processor.ProcessAsync(evt, cancellationToken);
        if (processed.IsFailure)
        {
            return processed.Error!;
        }

        return (await _orders.GetNoTrackingAsync(id, cancellationToken))!.ToDto();
    }

    /// <summary>Billing must be on and the caller a signed-in client USER (an API key never buys credits, whatever scopes it holds).</summary>
    private Error? Guard()
    {
        if (!_options.Enabled)
        {
            return new Error(ErrorCodes.BillingDisabled, "Online payments are not enabled.", ErrorType.NotFound);
        }

        if (_currentUser.ClientId is null || _currentUser.ActorType != ActorType.User)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Billing is available to signed-in client users only.");
        }

        return null;
    }

    private Result<CheckoutResponseDto> Replay(PaymentOrder existing, Guid packId)
    {
        if (existing.PackId != packId)
        {
            return Error.Conflict(ErrorCodes.Conflict, "This idempotency key was already used for a different credit pack.");
        }

        return existing is { Status: PaymentOrderStatus.Pending, CheckoutUrl: { } url } && existing.ExpiresAt > Now
            ? new CheckoutResponseDto(existing.Id, url, existing.ExpiresAt)
            : Error.Conflict(ErrorCodes.Conflict, "This checkout was already completed or closed. Start a new one with a new idempotency key.");
    }

    private Uri ReturnUrl(string path, Guid orderId) =>
        new(_portal.PublicBaseUrl.TrimEnd('/') + path.Replace("{orderId}", orderId.ToString("D"), StringComparison.Ordinal), UriKind.Absolute);

    private static bool TryStatus(string? text, out PaymentOrderStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (Enum.TryParse<PaymentOrderStatus>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            status = parsed;
            return true;
        }

        return false;
    }

    private static async IAsyncEnumerable<string> OrderLines(IReadOnlyList<OrderRow> rows)
    {
        yield return CsvFormat.Line("order_id", "invoice_number", "pack", "credits", "subtotal_minor", "tax_minor", "total_minor", "currency", "status", "created_at", "paid_at", "refunded_minor");
        foreach (var row in rows)
        {
            var o = row.Order;
            yield return CsvFormat.Line(
                o.Id.ToString("D"), o.InvoiceNumber, o.PackName, o.Credits, o.SubtotalMinor, o.TaxMinor, o.TotalMinor, o.Currency, o.Status.ToString(),
                o.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), o.PaidAt?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), o.RefundedMinor);
        }

        await Task.CompletedTask;
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Checkout created: order {OrderId} for client {ClientId} with {Provider}")]
    private partial void LogCheckoutCreated(Guid orderId, Guid clientId, string provider);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Payment provider call for order {OrderId} ({Provider}) failed: {Reason}")]
    private partial void LogCheckoutFailed(Guid orderId, string provider, string reason);
}
