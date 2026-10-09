using NexaVerify.Contracts.Common;

namespace NexaVerify.Web.ComponentTests;

public static class BillingSample
{
    public static readonly Guid PackStarter = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    public static readonly Guid PackGrowth = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid OrderId = Guid.Parse("00000000-0000-0000-0000-00000000d001");
    public static readonly DateTimeOffset Now = new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    // USD packs with 20 % VAT: 49.00 + 9.80 = 58.80 and 199.00 + 39.80 = 238.80
    public static CreditPackDto Starter() => new(PackStarter, "Starter pack", "For one site.", 5000, 365, 4900, "USD", 20m, "VAT", 5880, ["Email support"]);

    public static CreditPackDto Growth() => new(PackGrowth, "Growth pack", "For busy teams.", 25000, 365, 19900, "USD", 20m, "VAT", 23880, ["Priority support"]);

    public static OrderDto Order(string status = OrderStatuses.Paid, string? invoice = "NV-2026-001042", Guid? licenseId = null) =>
        new(OrderId, status == OrderStatuses.Paid ? invoice : null, "Starter pack", 5000, 365, 4900, 980, 5880, 20m, "VAT", "USD", status, Now.AddMinutes(-3),
            status == OrderStatuses.Paid ? Now : null, status == OrderStatuses.Paid ? licenseId ?? Guid.Parse("00000000-0000-0000-0000-00000000f001") : null);

    public static OrderListItemDto Item(string status = OrderStatuses.Paid, string? invoice = "NV-2026-001042") =>
        new(OrderId, invoice, "Starter pack", 5000, 5880, "USD", status, Now.AddDays(-1), status == OrderStatuses.Paid ? Now : null);

    public static BillingProfileDto CompleteProfile() =>
        new("Acme Corp Ltd", "1 High Street", null, "London", null, "EC1A 1BB", "GB", "GB123456789", "billing@acme.test", "AAAAAAAB");

    public static BillingProfileDto EmptyProfile() => new(null, null, null, null, null, null, null, null, null, null);

    public static AdminPackDto AdminPack(string name = "Starter pack", bool active = true) =>
        new(PackStarter, name, "For one site.", 5000, 365, 4900, "USD", ["Email support"], 1, active, true, "AAAAAAAC");

    public static AdminOrderDto AdminOrder(string status = OrderStatuses.Paid, IReadOnlyList<RefundDto>? refunds = null) =>
        new(OrderId, "NV-2026-001042", Guid.Parse("00000000-0000-0000-0000-00000000c001"), "Acme Corp", "Starter pack", 5000, 365, 4900, 980, 5880, 20m, "VAT", "USD",
            status, Now.AddDays(-2), Now.AddDays(-2), Guid.NewGuid(), "stripe", "pi_123", refunds ?? [], [new OrderEventDto("payment.succeeded", Now.AddDays(-2), "Payment confirmed")]);

    public static AdminOrderListItemDto AdminItem(string status = OrderStatuses.Paid) =>
        new(OrderId, "NV-2026-001042", Guid.NewGuid(), "Acme Corp", "Starter pack", 5000, 5880, "USD", status, Now.AddDays(-2), Now.AddDays(-2));
}

public sealed class FakeBillingApi : IBillingApiClient
{
    public List<string> Calls { get; } = [];

    public List<string> CheckoutKeys { get; } = [];

    public List<bool> PassiveFlags { get; } = [];

    public int OrderCalls { get; private set; }

    public Func<Task<ApiResult<IReadOnlyList<CreditPackDto>>>> Packs { get; set; } = () => Ok.Of<IReadOnlyList<CreditPackDto>>([BillingSample.Starter(), BillingSample.Growth()]);

    public Func<Guid, string, Task<ApiResult<CheckoutResponse>>> Checkout { get; set; } =
        (_, _) => Ok.Of(new CheckoutResponse(BillingSample.OrderId, "https://pay.example.test/session/abc", BillingSample.Now.AddMinutes(30)));

    public Func<PageRequest, string?, Task<ApiResult<PagedResult<OrderListItemDto>>>> Orders { get; set; } = (_, _) => Task.FromResult(Ok.Page(BillingSample.Item()));

    /// <summary>Answer per call number (1-based), so a test can script Pending, Pending, Paid.</summary>
    public Func<int, Task<ApiResult<OrderDto>>> Order { get; set; } = _ => Ok.Of(BillingSample.Order());

    public Func<Task<ApiResult<BillingProfileDto>>> Profile { get; set; } = () => Ok.Of(BillingSample.CompleteProfile());

    public Func<SaveBillingProfileRequest, Task<ApiResult<BillingProfileDto>>> Save { get; set; } = r =>
        Ok.Of(new BillingProfileDto(r.LegalName, r.AddressLine1, r.AddressLine2, r.City, r.State, r.PostalCode, r.Country, r.TaxId, r.BillingEmail, "BBBBBBBB"));

    public SaveBillingProfileRequest? LastSave { get; private set; }

    public Task<ApiResult<IReadOnlyList<CreditPackDto>>> GetPacksAsync(CancellationToken ct = default)
    {
        Calls.Add("packs");
        return Packs();
    }

    public Task<ApiResult<CheckoutResponse>> CheckoutAsync(Guid packId, string idempotencyKey, CancellationToken ct = default)
    {
        Calls.Add("checkout:" + packId);
        CheckoutKeys.Add(idempotencyKey);
        return Checkout(packId, idempotencyKey);
    }

    public Task<ApiResult<PagedResult<OrderListItemDto>>> ListOrdersAsync(PageRequest page, string? status, CancellationToken ct = default)
    {
        Calls.Add($"orders:{status}:{page.Page}");
        return Orders(page, status);
    }

    public Task<ApiResult<OrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default, bool passive = false)
    {
        OrderCalls++;
        PassiveFlags.Add(passive);
        return Order(OrderCalls);
    }

    public Task<ApiResult<BillingProfileDto>> GetProfileAsync(CancellationToken ct = default) => Profile();

    public Task<ApiResult<BillingProfileDto>> SaveProfileAsync(SaveBillingProfileRequest request, CancellationToken ct = default)
    {
        LastSave = request;
        Calls.Add("save-profile");
        return Save(request);
    }
}

public sealed class FakeAdminBillingApi : IAdminBillingApiClient
{
    public List<string> Calls { get; } = [];

    public SaveAdminPackRequest? LastPack { get; private set; }

    public RefundOrderRequest? LastRefund { get; private set; }

    public AdminOrderQuery? LastQuery { get; private set; }

    public Func<Task<ApiResult<IReadOnlyList<AdminPackDto>>>> Packs { get; set; } = () => Ok.Of<IReadOnlyList<AdminPackDto>>([BillingSample.AdminPack(), BillingSample.AdminPack("Retired pack", active: false)]);

    public Func<AdminOrderQuery, Task<ApiResult<PagedResult<AdminOrderListItemDto>>>> Orders { get; set; } = _ => Task.FromResult(Ok.Page(BillingSample.AdminItem()));

    public Func<Task<ApiResult<AdminOrderDto>>> Order { get; set; } = () => Ok.Of(BillingSample.AdminOrder());

    public Func<RefundOrderRequest, Task<ApiResult<AdminOrderDto>>> Refund { get; set; } = r =>
        Ok.Of(BillingSample.AdminOrder(OrderStatuses.PartiallyRefunded, [new RefundDto(Guid.NewGuid(), r.AmountMinor ?? 5880, 5000, r.Reason, BillingSample.Now)]));

    public Task<ApiResult<IReadOnlyList<AdminPackDto>>> ListPacksAsync(CancellationToken ct = default) => Packs();

    public Func<SaveAdminPackRequest, Task<ApiResult<AdminPackDto>>>? CreatePack { get; set; }

    public Task<ApiResult<AdminPackDto>> CreatePackAsync(SaveAdminPackRequest request, CancellationToken ct = default)
    {
        LastPack = request;
        Calls.Add("pack-create:" + request.Name);
        return CreatePack?.Invoke(request) ?? Ok.Of(BillingSample.AdminPack(request.Name));
    }

    public Task<ApiResult<AdminPackDto>> UpdatePackAsync(Guid id, SaveAdminPackRequest request, CancellationToken ct = default)
    {
        LastPack = request;
        Calls.Add($"pack-update:{request.Name}:{request.IsActive}");
        return Ok.Of(BillingSample.AdminPack(request.Name, request.IsActive));
    }

    public Task<ApiResult<PagedResult<AdminOrderListItemDto>>> ListOrdersAsync(AdminOrderQuery query, CancellationToken ct = default)
    {
        LastQuery = query;
        Calls.Add($"orders:{query.Status}:{query.ClientId}");
        return Orders(query);
    }

    public Task<ApiResult<AdminOrderDto>> GetOrderAsync(Guid id, CancellationToken ct = default) => Order();

    public Task<ApiResult<AdminOrderDto>> RefundAsync(Guid id, RefundOrderRequest request, CancellationToken ct = default)
    {
        LastRefund = request;
        Calls.Add("refund");
        return Refund(request);
    }

    public Task<ApiResult<AdminOrderDto>> ReconcileAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("reconcile");
        return Order();
    }
}

public sealed class FakeDevBillingApi : IDevBillingApiClient
{
    public List<string> Outcomes { get; } = [];

    public Task<ApiResult<bool>> SimulateAsync(Guid orderId, string outcome, CancellationToken ct = default)
    {
        Outcomes.Add(outcome);
        return Ok.Of(true);
    }
}

/// <summary>Billing page setup: a client admin with the billing permissions, fake billing APIs, dialogs and a fixed clock.</summary>
public abstract class BillingPageTestBase : ClientPageTestBase
{
    protected BillingPageTestBase()
    {
        Billing = new FakeBillingApi();
        AdminBilling = new FakeAdminBillingApi();
        DevBilling = new FakeDevBillingApi();
        Services.AddSingleton<IBillingApiClient>(Billing);
        Services.AddSingleton<IAdminBillingApiClient>(AdminBilling);
        Services.AddSingleton<IDevBillingApiClient>(DevBilling);
    }

    protected FakeBillingApi Billing { get; }

    protected FakeAdminBillingApi AdminBilling { get; }

    protected FakeDevBillingApi DevBilling { get; }

    protected void SignInAsBilling(params string[] extra) =>
        User.SignIn("Una Admin", "una@acme.test", "ClientAdmin", "Acme Corp", [WebPermissions.BillingRead, WebPermissions.BillingManage, .. extra]);

    protected void SignInAsBillingReader() =>
        User.SignIn("Rae Reader", "rae@acme.test", "ClientUser", "Acme Corp", [WebPermissions.BillingRead]);

    protected void SignInAsBillingStaff(params string[] permissions) =>
        User.SignIn("Ada Admin", "ada@nexaverify.test", "SuperAdmin", null, permissions);

    protected string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;
}
