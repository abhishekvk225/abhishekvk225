using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Billing;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Api;
using NexaVerify.Domain.Billing;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Billing;

public interface IPublicBillingService
{
    /// <summary>Public, active packs in allowed currencies; an empty list while billing is off.</summary>
    Task<Result<IReadOnlyList<PublicPackDto>>> GetPacksAsync(CancellationToken cancellationToken);
}

public sealed class PublicBillingService : IPublicBillingService
{
    private readonly ICreditPackRepository _packs;
    private readonly BillingOptions _options;

    public PublicBillingService(ICreditPackRepository packs, IOptions<BillingOptions> options)
    {
        _packs = packs;
        _options = options.Value;
    }

    public async Task<Result<IReadOnlyList<PublicPackDto>>> GetPacksAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Result<IReadOnlyList<PublicPackDto>>.Success([]);
        }

        var packs = await _packs.ListAsync(activeOnly: true, publicOnly: true, cancellationToken);
        return Result<IReadOnlyList<PublicPackDto>>.Success(packs.Where(p => _options.AllowsCurrency(p.Currency)).Select(p => p.ToPublicDto(_options)).ToList());
    }
}

public interface IBillingAdminService
{
    AdminBillingConfigDto GetConfig();

    Task<Result<IReadOnlyList<AdminCreditPackDto>>> ListPacksAsync(CancellationToken cancellationToken);

    Task<Result<AdminCreditPackDto>> GetPackAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<AdminCreditPackDto>> CreatePackAsync(CreateCreditPackRequest request, CancellationToken cancellationToken);

    Task<Result<AdminCreditPackDto>> UpdatePackAsync(Guid id, UpdateCreditPackRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a pack nobody ever bought; a pack with orders can only be switched off (409).</summary>
    Task<Result> DeletePackAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<PagedResult<AdminOrderListItemDto>>> ListOrdersAsync(AdminOrderQuery query, CancellationToken cancellationToken);

    Task<Result<AdminOrderDto>> GetOrderAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<AdminOrderDto>> RefundAsync(Guid id, RefundOrderRequest request, CancellationToken cancellationToken);

    Task<Result<AdminOrderDto>> ReconcileAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Platform staff side of billing: the pack catalogue, every client's orders, refunds and manual reconciliation.</summary>
public sealed partial class BillingAdminService : IBillingAdminService
{
    private readonly ICreditPackRepository _packs;
    private readonly IPaymentOrderRepository _orders;
    private readonly IPaymentOrderAtomics _atomics;
    private readonly IPaymentProviderResolver _providers;
    private readonly IPaymentReconciler _reconciler;
    private readonly ILicenseGrantService _grants;
    private readonly IWebhookPublisher _webhooks;
    private readonly ILicenseAlertRepository _alerts;
    private readonly IClientRepository _clients;
    private readonly IAuditService _audit;
    private readonly IEmailOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantScope _scope;
    private readonly ICurrentUser _currentUser;
    private readonly BillingOptions _options;
    private readonly PortalLinksOptions _portal;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingAdminService> _logger;

    public BillingAdminService(
        ICreditPackRepository packs,
        IPaymentOrderRepository orders,
        IPaymentOrderAtomics atomics,
        IPaymentProviderResolver providers,
        IPaymentReconciler reconciler,
        ILicenseGrantService grants,
        IWebhookPublisher webhooks,
        ILicenseAlertRepository alerts,
        IClientRepository clients,
        IAuditService audit,
        IEmailOutbox outbox,
        IUnitOfWork unitOfWork,
        ITenantScope scope,
        ICurrentUser currentUser,
        IOptions<BillingOptions> options,
        IOptions<PortalLinksOptions> portal,
        TimeProvider time,
        ILogger<BillingAdminService> logger)
    {
        _packs = packs;
        _orders = orders;
        _atomics = atomics;
        _providers = providers;
        _reconciler = reconciler;
        _grants = grants;
        _webhooks = webhooks;
        _alerts = alerts;
        _clients = clients;
        _audit = audit;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _scope = scope;
        _currentUser = currentUser;
        _options = options.Value;
        _portal = portal.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public AdminBillingConfigDto GetConfig() => new(
        _options.Enabled,
        _options.Provider,
        _options.Mode(),
        _options.TaxPercent,
        _options.TaxLabel,
        _options.AllowedCurrencies.Select(c => c.ToUpperInvariant()).Distinct().ToList(),
        _options.RequireBillingProfile);

    public async Task<Result<IReadOnlyList<AdminCreditPackDto>>> ListPacksAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<AdminCreditPackDto>>.Success((await _packs.ListAsync(activeOnly: false, publicOnly: false, cancellationToken)).Select(p => p.ToAdminDto()).ToList());

    public async Task<Result<AdminCreditPackDto>> GetPackAsync(Guid id, CancellationToken cancellationToken) =>
        await _packs.GetAsync(id, cancellationToken) is { } pack ? pack.ToAdminDto() : Error.NotFound("The credit pack was not found.");

    public async Task<Result<AdminCreditPackDto>> CreatePackAsync(CreateCreditPackRequest request, CancellationToken cancellationToken)
    {
        CreditPack pack;
        try
        {
            pack = CreditPack.Create(request.Name, request.Description, request.Credits, request.ValidityDays, request.PriceMinor, request.Currency, request.Highlights, request.DisplayOrder, request.IsActive, request.IsPublic);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        _packs.Add(pack);
        _audit.Record(new AuditEntry("billing.pack_created", nameof(CreditPack), pack.Id.ToString(),
            NewValues: new { pack.Name, pack.Credits, pack.ValidityDays, pack.PriceMinor, pack.Currency, pack.IsActive, pack.IsPublic }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return pack.ToAdminDto();
    }

    public async Task<Result<AdminCreditPackDto>> UpdatePackAsync(Guid id, UpdateCreditPackRequest request, CancellationToken cancellationToken)
    {
        var pack = await _packs.GetAsync(id, cancellationToken);
        if (pack is null)
        {
            return Error.NotFound("The credit pack was not found.");
        }

        var before = new { pack.Name, pack.Credits, pack.ValidityDays, pack.PriceMinor, pack.Currency, pack.IsActive, pack.IsPublic };
        try
        {
            pack.Update(request.Name, request.Description, request.Credits, request.ValidityDays, request.PriceMinor, request.Currency, request.Highlights, request.DisplayOrder, request.IsActive, request.IsPublic);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        // Orders keep their own snapshot, so editing a price never changes an order that already exists.
        _audit.Record(new AuditEntry("billing.pack_updated", nameof(CreditPack), pack.Id.ToString(), OldValues: before,
            NewValues: new { pack.Name, pack.Credits, pack.ValidityDays, pack.PriceMinor, pack.Currency, pack.IsActive, pack.IsPublic }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return pack.ToAdminDto();
    }

    public async Task<Result> DeletePackAsync(Guid id, CancellationToken cancellationToken)
    {
        var pack = await _packs.GetAsync(id, cancellationToken);
        if (pack is null)
        {
            return Error.NotFound("The credit pack was not found.");
        }

        if (await _packs.HasOrdersAsync(id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "This pack has been ordered. Switch it off instead of deleting it.");
        }

        _packs.Remove(pack);
        _audit.Record(new AuditEntry("billing.pack_deleted", nameof(CreditPack), pack.Id.ToString(), OldValues: new { pack.Name, pack.PriceMinor, pack.Currency }));
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is UniqueConstraintViolationException or ConcurrencyConflictException)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ErrorCodes.Conflict, "The pack changed at the same time. Reload and try again.");
        }

        return Result.Success();
    }

    public async Task<Result<PagedResult<AdminOrderListItemDto>>> ListOrdersAsync(AdminOrderQuery query, CancellationToken cancellationToken)
    {
        PaymentOrderStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<PaymentOrderStatus>(query.Status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Error.Validation("Unknown status filter.");
            }

            status = parsed;
        }

        var paging = new PageRequest { Page = query.Page, PageSize = query.PageSize }.Normalize();
        var (items, total) = await _orders.ListAsync(query.ClientId, status, Utc(query.From), Utc(query.To), paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<AdminOrderListItemDto>(items.Select(r => r.ToAdminListItem()).ToList(), paging.Page, paging.PageSize, total);
    }

    public async Task<Result<AdminOrderDto>> GetOrderAsync(Guid id, CancellationToken cancellationToken) =>
        await LoadDetailAsync(id, cancellationToken) is { } detail ? detail : Error.NotFound("The order was not found.");

    public async Task<Result<AdminOrderDto>> ReconcileAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await _orders.GetNoTrackingAsync(id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("The order was not found.");
        }

        var reconciled = await _reconciler.ReconcileAsync(id, cancellationToken);
        if (reconciled.IsFailure)
        {
            return reconciled.Error!;
        }

        _audit.Record(new AuditEntry("billing.order_reconciled", nameof(PaymentOrder), id.ToString(), order.ClientId, NewValues: new { Outcome = reconciled.Value ?? "NothingToDo" }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return (await LoadDetailAsync(id, cancellationToken))!;
    }

    public async Task<Result<AdminOrderDto>> RefundAsync(Guid id, RefundOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await _orders.GetNoTrackingAsync(id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound("The order was not found.");
        }

        if (!order.IsRefundable || string.IsNullOrEmpty(order.ProviderPaymentId))
        {
            return Error.Conflict(ErrorCodes.OrderNotRefundable, "Only a paid order that is not fully refunded can be refunded.");
        }

        var amount = request.AmountMinor ?? order.RefundableMinor;
        if (amount <= 0 || amount > order.RefundableMinor)
        {
            return Error.Validation(
                "The refund amount must be positive and not exceed what is left to refund.",
                new Dictionary<string, string[]> { ["amountMinor"] = [$"Between 1 and {order.RefundableMinor}."] });
        }

        var provider = _providers.Find(order.Provider);
        if (provider is null)
        {
            return Error.Unavailable(ErrorCodes.PaymentProviderUnavailable, "The payment provider of this order is not configured.");
        }

        // Everything below touches the client's license, ledger and webhooks, which are tenant-filtered.
        using var tenant = _scope.BeginTenant(order.ClientId);
        var reason = request.Reason.Trim();
        var now = Now;

        // 1. Reserve the amount and write the Pending refund BEFORE calling the provider: two admins can not refund the same money twice.
        Refund? refund = null;
        var reserved = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                if (!await _atomics.TryReserveRefundAsync(order.Id, amount, now, ct))
                {
                    return false;
                }

                refund = Refund.Start(order.ClientId, order.Id, amount, reason);
                _orders.Add(refund);
                _audit.Record(new AuditEntry("billing.refund_requested", nameof(PaymentOrder), order.Id.ToString(), order.ClientId,
                    NewValues: new { RefundId = refund.Id, AmountMinor = amount, Reason = reason }));
                await _unitOfWork.SaveChangesAsync(ct);
                return true;
            },
            cancellationToken);
        if (!reserved || refund is null)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ErrorCodes.OrderNotRefundable, "The order was refunded or changed in the meantime. Reload it.");
        }

        // 2. The provider moves the money. The refund id is the idempotency key, so a retry after a crash can not pay out twice.
        ProviderRefund providerRefund;
        try
        {
            providerRefund = await provider.RefundAsync(order.ProviderPaymentId, amount, order.Currency, reason, refund.Id.ToString("N"), cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            LogRefundFailed(order.Id, order.Provider, ex.Message);
            await _unitOfWork.ExecuteInTransactionAsync(
                async ct =>
                {
                    await _atomics.ReleaseRefundAsync(order.Id, amount, Now, ct);
                    var failed = await _orders.GetRefundAsync(refund.Id, ct);
                    failed?.Fail(Now);
                    _audit.Record(new AuditEntry("billing.refund_failed", nameof(PaymentOrder), order.Id.ToString(), order.ClientId, NewValues: new { RefundId = refund.Id, AmountMinor = amount }));
                    await _unitOfWork.SaveChangesAsync(ct);
                    return true;
                },
                CancellationToken.None);
            return Error.Unavailable(ErrorCodes.PaymentProviderUnavailable, "The payment provider refused or could not process the refund. Nothing was changed.");
        }

        // 3. Take back the credits the customer has not used yet, in proportion to the money returned (cumulative, rounded once).
        var current = (await _orders.GetNoTrackingAsync(order.Id, CancellationToken.None))!;
        var targetRevoked = (int)Math.Round((decimal)order.Credits * current.RefundedMinor / order.TotalMinor, 0, MidpointRounding.AwayFromZero);
        var toRevoke = Math.Max(0, targetRevoked - current.CreditsRevoked);
        var revoked = 0;
        if (toRevoke > 0 && order.LicenseId is { } licenseId)
        {
            var taken = await _grants.RevokeUnusedCreditsAsync(licenseId, toRevoke, $"Refund of order {order.InvoiceNumber ?? order.Id.ToString("D")}", CancellationToken.None);
            if (taken.IsSuccess)
            {
                revoked = taken.Value;
            }
            else
            {
                LogRevokeFailed(order.Id, licenseId, taken.Error!.Code); // the money is already back: record it, flag the credits for a human
            }
        }

        // 4. Record the outcome.
        await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var done = await _orders.GetRefundAsync(refund.Id, ct);
                done?.Succeed(providerRefund.RefundId, revoked, Now);
                await _atomics.CompleteRefundAsync(order.Id, revoked, Now, ct);
                _audit.Record(new AuditEntry("billing.refund", nameof(PaymentOrder), order.Id.ToString(), order.ClientId,
                    NewValues: new { RefundId = refund.Id, AmountMinor = amount, CreditsRevoked = revoked, Reason = reason, order.Currency }));
                if (revoked > 0)
                {
                    await _webhooks.PublishAsync(order.ClientId, WebhookEvents.LicenseCreditsRevoked, new
                    {
                        orderId = order.Id,
                        licenseId = order.LicenseId,
                        creditsRevoked = revoked,
                        refundedMinor = amount,
                        currency = order.Currency,
                    }, ct);
                }

                _alerts.Add(LicenseAlert.Create(
                    order.ClientId, LicenseAlertType.PaymentRefunded, refund.Id, "refund", AlertSeverity.Info, "Payment refunded",
                    $"{Money.Format(amount, order.Currency)} of your purchase \"{order.PackName}\" was refunded; {revoked} unused credits were taken back.", Now));
                await _unitOfWork.SaveChangesAsync(ct);
                return true;
            },
            CancellationToken.None);

        await NotifyRefundAsync(order, amount, revoked, cancellationToken);
        LogRefunded(order.Id, order.ClientId, amount, revoked);
        return (await LoadDetailAsync(id, cancellationToken))!;
    }

    private async Task NotifyRefundAsync(PaymentOrder order, long amount, int revoked, CancellationToken cancellationToken)
    {
        var to = BuyerSnapshot.Parse(order.BuyerJson).BillingEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            to = (await _clients.GetByIdAsync(order.ClientId, cancellationToken))?.ContactEmail ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        _outbox.Enqueue(new EmailMessage(
            to,
            $"Refund issued - {order.InvoiceNumber ?? "your order"}",
            $"Hello,\n\nWe refunded {Money.Format(amount, order.Currency)} of your purchase \"{order.PackName.ReplaceLineEndings(" ")}\" ({order.InvoiceNumber}). "
            + $"{revoked.ToString("N0", CultureInfo.InvariantCulture)} unused credits were taken back from your account.\n\n"
            + $"Details: {_portal.LinkTo($"billing/orders/{order.Id:D}")}"));
    }

    private async Task<AdminOrderDto?> LoadDetailAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _orders.GetRowAsync(id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var refunds = await _orders.GetRefundsAsync(id, cancellationToken);
        var o = row.Order;
        return new AdminOrderDto(o.ToDto(), o.ClientId, row.ClientName, o.Provider, o.ProviderSessionId, o.ProviderPaymentId, o.CreditsRevoked, o.FailureReason,
            refunds.OrderBy(r => r.CreatedAt).Select(r => r.ToDto()).ToList());
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    [LoggerMessage(EventId = 9007, Level = LogLevel.Information, Message = "Order {OrderId} of client {ClientId} refunded {AmountMinor} (minor units); {CreditsRevoked} unused credits taken back")]
    private partial void LogRefunded(Guid orderId, Guid clientId, long amountMinor, int creditsRevoked);

    [LoggerMessage(EventId = 9008, Level = LogLevel.Warning, Message = "Refund for order {OrderId} ({Provider}) failed at the provider: {Reason}")]
    private partial void LogRefundFailed(Guid orderId, string provider, string reason);

    [LoggerMessage(EventId = 9014, Level = LogLevel.Error, Message = "Refund of order {OrderId} was paid out but the credits of license {LicenseId} could not be taken back ({Code}); fix the balance by hand")]
    private partial void LogRevokeFailed(Guid orderId, Guid licenseId, string code);
}
