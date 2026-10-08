using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

/// <summary>A task you complete by hand, to freeze a page in its loading state.</summary>
public sealed class Gate<T>
{
    private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => _source.Task;

    public void Release(T value) => _source.SetResult(value);
}

public sealed class FakeClientsApi : IClientsApiClient
{
    public List<string> Calls { get; } = [];

    public Func<PageRequest, string?, Task<ApiResult<PagedResult<ClientListItemDto>>>> List { get; set; } =
        (_, _) => Task.FromResult(ApiResult<PagedResult<ClientListItemDto>>.Ok(new PagedResult<ClientListItemDto>([], 1, 25, 0)));

    public Func<Guid, Task<ApiResult<ClientDto>>> Get { get; set; } = id => Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(id)));

    public Func<Guid, string?, Task<ApiResult<ClientDto>>> Suspend { get; set; } = (id, _) => Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(id) with { Status = "Suspended" }));

    public Func<CreateClientRequest, Task<ApiResult<ClientDto>>> Create { get; set; } = r => Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(Guid.NewGuid()) with { Name = r.Name, Code = r.Code }));

    public Task<ApiResult<PagedResult<ClientListItemDto>>> ListAsync(PageRequest page, string? status, CancellationToken ct = default)
    {
        Calls.Add($"list:{page.Search}:{status}:{page.Page}");
        return List(page, status);
    }

    public Task<ApiResult<ClientDto>> GetAsync(Guid id, CancellationToken ct = default) => Get(id);

    public Task<ApiResult<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken ct = default)
    {
        Calls.Add("create:" + request.Code);
        return Create(request);
    }

    public Task<ApiResult<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct = default) => Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(id)));

    public Task<ApiResult<ClientDto>> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("activate");
        return Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(id)));
    }

    public Task<ApiResult<ClientDto>> DeactivateAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        Calls.Add("deactivate:" + reason);
        return Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(id) with { Status = "Inactive" }));
    }

    public Task<ApiResult<ClientDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        Calls.Add("suspend:" + reason);
        return Suspend(id, reason);
    }

    public Task<ApiResult<PagedResult<ClientUserDto>>> ListUsersAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PagedResult<ClientUserDto>>.Ok(new PagedResult<ClientUserDto>(
            [new ClientUserDto(Guid.NewGuid(), "una@acme.test", "Una User", "Active", "ClientAdmin", null, true, null, false, Sample.Now)], 1, 10, 1)));

    public Task<ApiResult<bool>> ResetUserPasswordAsync(Guid clientId, Guid userId, CancellationToken ct = default)
    {
        Calls.Add("reset");
        return Task.FromResult(ApiResult<bool>.Ok(true));
    }

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetActivityAsync(Guid id, ActivityQuery query, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PagedResult<AuditLogDto>>.Ok(new PagedResult<AuditLogDto>([], 1, 25, 0)));

    public Task<ApiResult<PagedResult<LoginHistoryDto>>> GetLoginsAsync(Guid id, ActivityQuery query, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PagedResult<LoginHistoryDto>>.Ok(new PagedResult<LoginHistoryDto>([], 1, 25, 0)));

    public Task<ApiResult<IReadOnlyList<SettingDto>>> GetSettingsAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<SettingDto>>.Ok([]));

    public Task<ApiResult<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(Guid id, UpdateSettingsRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<SettingDto>>.Ok([]));
}

public sealed class FakeLicensingApi : ILicensingApiClient
{
    public List<string> Calls { get; } = [];

    public Func<LicenseDto> License { get; set; } = () => Sample.License();

    public Func<Task<ApiResult<LedgerVerificationReportDto>>> VerifyAll { get; set; } =
        () => Task.FromResult(ApiResult<LedgerVerificationReportDto>.Ok(new LedgerVerificationReportDto(Sample.Now, Sample.Now, 3, 40, 0, [])));

    public Func<Task<ApiResult<IReadOnlyList<PlanDto>>>> Plans { get; set; } = () => Task.FromResult(ApiResult<IReadOnlyList<PlanDto>>.Ok([Sample.Plan()]));

    public Task<ApiResult<PagedResult<LicenseListItemDto>>> ListAsync(PageRequest page, string? status, Guid? clientId, int? expiringInDays, CancellationToken ct = default)
    {
        Calls.Add($"list:{status}:{clientId}:{expiringInDays}");
        return Task.FromResult(ApiResult<PagedResult<LicenseListItemDto>>.Ok(new PagedResult<LicenseListItemDto>([Sample.LicenseItem()], 1, 25, 1)));
    }

    public Task<ApiResult<LicenseDto>> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(ApiResult<LicenseDto>.Ok(License()));

    public Task<ApiResult<LicenseDto>> CreateAsync(Guid clientId, CreateLicenseRequest request, CancellationToken ct = default)
    {
        Calls.Add("create:" + request.Name);
        return Task.FromResult(ApiResult<LicenseDto>.Ok(License()));
    }

    public Task<ApiResult<LicenseDto>> UpdateAsync(Guid id, UpdateLicenseRequest request, CancellationToken ct = default) => Task.FromResult(ApiResult<LicenseDto>.Ok(License()));

    public Task<ApiResult<LicenseDto>> ActivateAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("activate");
        return Task.FromResult(ApiResult<LicenseDto>.Ok(License()));
    }

    public Task<ApiResult<LicenseDto>> DeactivateAsync(Guid id, CancellationToken ct = default) => Task.FromResult(ApiResult<LicenseDto>.Ok(License()));

    public Task<ApiResult<LicenseDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        Calls.Add("suspend:" + reason);
        return Task.FromResult(ApiResult<LicenseDto>.Ok(License() with { Status = "Suspended", EffectiveStatus = "Suspended" }));
    }

    public Task<ApiResult<LicenseDto>> RevokeAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        Calls.Add("revoke:" + reason);
        return Task.FromResult(ApiResult<LicenseDto>.Ok(License() with { Status = "Revoked", EffectiveStatus = "Revoked" }));
    }

    public Task<ApiResult<LicenseDto>> RenewAsync(Guid id, RenewLicenseRequest request, CancellationToken ct = default) => Task.FromResult(ApiResult<LicenseDto>.Ok(License()));

    public Task<ApiResult<LicenseDto>> AdjustAsync(Guid id, AdjustLicenseRequest request, CancellationToken ct = default)
    {
        Calls.Add($"adjust:{request.Credits}:{request.Reason}");
        return Task.FromResult(ApiResult<LicenseDto>.Ok(License()));
    }

    public Task<ApiResult<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PagedResult<LicenseTransactionDto>>.Ok(new PagedResult<LicenseTransactionDto>(
            [new LicenseTransactionDto(7, id, "Consume", -2, 100, 98, "Verify", null, null, null, "ApiKey", null, Sample.Now)], 1, 25, 1)));

    public Task<ApiResult<LicenseTransactionDto>> RefundAsync(long transactionId, RefundRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<LicenseTransactionDto>.Ok(new LicenseTransactionDto(8, Guid.Empty, "Refund", 2, 98, 100, null, null, transactionId, request.Reason, "User", null, Sample.Now)));

    public Task<ApiResult<LedgerVerificationDto>> VerifyLedgerAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<LedgerVerificationDto>.Ok(new LedgerVerificationDto(id, true, 12, [])));

    public Task<ApiResult<LedgerVerificationReportDto>> VerifyAllLedgersAsync(CancellationToken ct = default)
    {
        Calls.Add("verify-all");
        return VerifyAll();
    }

    public Task<ApiResult<IReadOnlyList<PlanDto>>> ListPlansAsync(CancellationToken ct = default) => Plans();

    public Task<ApiResult<PlanDto>> CreatePlanAsync(SavePlanRequest request, CancellationToken ct = default)
    {
        Calls.Add("plan-create:" + request.Code);
        return Task.FromResult(ApiResult<PlanDto>.Ok(Sample.Plan()));
    }

    public Task<ApiResult<PlanDto>> UpdatePlanAsync(Guid id, SavePlanRequest request, CancellationToken ct = default) => Task.FromResult(ApiResult<PlanDto>.Ok(Sample.Plan()));

    public Task<ApiResult<IReadOnlyList<CostRuleDto>>> ListCostRulesAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<CostRuleDto>>.Ok([new CostRuleDto(Guid.NewGuid(), "Platform", null, null, "Verify", 2, "OnCompleted", Sample.Now, null)]));

    public Task<ApiResult<CostRuleDto>> SetDefaultCostRuleAsync(SetCostRuleRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<CostRuleDto>.Ok(new CostRuleDto(Guid.NewGuid(), "Platform", null, null, request.Operation, request.Credits, request.ChargePolicy, Sample.Now, null)));

    public Task<ApiResult<CostRuleDto>> SetPlanCostRuleAsync(Guid planId, SetCostRuleRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<CostRuleDto>.Ok(new CostRuleDto(Guid.NewGuid(), "Plan", null, planId, request.Operation, request.Credits, request.ChargePolicy, Sample.Now, null)));

    public Task<ApiResult<IReadOnlyList<CostRuleDto>>> ListClientCostRulesAsync(Guid clientId, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<CostRuleDto>>.Ok([]));

    public Task<ApiResult<CostRuleDto>> SetClientCostRuleAsync(Guid clientId, SetCostRuleRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<CostRuleDto>.Ok(new CostRuleDto(Guid.NewGuid(), "Client", clientId, null, request.Operation, request.Credits, request.ChargePolicy, Sample.Now, null)));
}

public sealed class FakeAccessApi : IAccessApiClient
{
    public Task<ApiResult<PagedResult<PlatformUserDto>>> ListUsersAsync(PageRequest page, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PagedResult<PlatformUserDto>>.Ok(new PagedResult<PlatformUserDto>(
            [new PlatformUserDto(Guid.NewGuid(), "ada@nexaverify.test", "Ada Admin", "Active", false, null, ["SuperAdmin"])], 1, 25, 1)));

    public Task<ApiResult<PlatformUserDto>> CreateUserAsync(CreatePlatformUserRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PlatformUserDto>.Ok(new PlatformUserDto(Guid.NewGuid(), request.Email, request.FullName, "Active", true, null, request.Roles)));

    public Task<ApiResult<PlatformUserDto>> UpdateUserAsync(Guid id, UpdatePlatformUserRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PlatformUserDto>.Ok(new PlatformUserDto(id, "x@y.test", request.FullName, "Active", false, null, request.Roles)));

    public Func<Task<ApiResult<IReadOnlyList<RoleDto>>>> Roles { get; set; } = () => Task.FromResult(ApiResult<IReadOnlyList<RoleDto>>.Ok(
    [
        new RoleDto(Guid.NewGuid(), "SuperAdmin", "Platform", true, "Everything", ["clients.read", "clients.create", "dashboard.admin"]),
        new RoleDto(Guid.NewGuid(), "Support", "Platform", false, null, ["clients.read"]),
        new RoleDto(Guid.NewGuid(), "ClientAdmin", "Client", true, null, ["dashboard.client"]),
    ]));

    public Task<ApiResult<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken ct = default) => Roles();

    public Task<ApiResult<RoleDto>> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<RoleDto>.Ok(new RoleDto(Guid.NewGuid(), request.Name, request.Scope, false, request.Description, request.Permissions)));

    public Task<ApiResult<RoleDto>> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<RoleDto>.Ok(new RoleDto(id, "x", "Platform", false, request.Description, request.Permissions)));

    public Task<ApiResult<IReadOnlyList<PermissionDto>>> ListPermissionsAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<PermissionDto>>.Ok(
        [
            new PermissionDto(Guid.NewGuid(), "clients.read", "Clients", PermissionScopeKind.Platform, "View clients"),
            new PermissionDto(Guid.NewGuid(), "clients.create", "Clients", PermissionScopeKind.Platform, "Create clients"),
            new PermissionDto(Guid.NewGuid(), "dashboard.admin", "Dashboard", PermissionScopeKind.Platform, "View the admin dashboard"),
            new PermissionDto(Guid.NewGuid(), "dashboard.client", "Dashboard", PermissionScopeKind.Client, "View the client dashboard"),
        ]));
}

public sealed class FakeDashboardApi : IDashboardApiClient
{
    public List<DashboardRange> AdminRanges { get; } = [];

    public Func<DashboardRange, Task<ApiResult<AdminDashboardModel>>> Admin { get; set; } = _ => Task.FromResult(ApiResult<AdminDashboardModel>.Ok(Sample.AdminDashboard()));

    public Func<DashboardRange, Task<ApiResult<ClientDashboardModel>>> Client { get; set; } = _ => Task.FromResult(ApiResult<ClientDashboardModel>.Ok(Sample.ClientDashboard()));

    public Task<ApiResult<AdminDashboardModel>> GetAdminDashboardAsync(DashboardRange range, CancellationToken ct = default)
    {
        AdminRanges.Add(range);
        return Admin(range);
    }

    public Task<ApiResult<ClientDashboardModel>> GetClientDashboardAsync(DashboardRange range, CancellationToken ct = default) => Client(range);
}

public static class Sample
{
    public static readonly DateTime Now = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    public static ClientDto Client(Guid id) => new(
        id, "ACME", "Acme Corp", "Acme Corporation Ltd", "ops@acme.test", "+44 20 7946 0000", "1 High Street", null, "London", null, "EC1A 1BB", "GB",
        "https://acme.test", "Retail", "Europe/London", "Active", null, null, "vip", Now, null, "AAAAAAAB");

    public static ClientListItemDto ClientItem(string name = "Acme Corp", string status = "Active") =>
        new(Guid.NewGuid(), name[..Math.Min(4, name.Length)].ToUpperInvariant(), name, "ops@acme.test", status, "GB", Now, 3);

    public static PlanDto Plan() => new(Guid.NewGuid(), "BUSINESS", "Business", null, 1000, 365, 60, null, null, 5, 10, true);

    public static LicenseListItemDto LicenseItem() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Acme Corp", "NXV-AAAA", "Annual 2026", "Business", "Active", 1000, 158, 842, Now.AddDays(-10), Now.AddDays(23), 23);

    public static LicenseDto License(string status = "Active") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Acme Corp", "NXV-AAAA-BBBB", "Annual 2026", Guid.NewGuid(), "Business", status, status, 1000, 158, 842, 16,
            Now.AddDays(-10), Now.AddDays(23), 23, null, null, "<b>notes</b>", Now, null, "AAAAAAAC");

    public static AdminDashboardModel AdminDashboard() => new(
        [new KpiModel("Active clients", "128", "i", null, true, null), new KpiModel("API requests", "12,480", "i", null, true, [1, 2, 3])],
        [new TrendSeries("Successful", [new TrendPoint("1 Jun", 10), new TrendPoint("2 Jun", 12)])],
        [new TrendSeries("Credits used", [new TrendPoint("1 Jun", 5), new TrendPoint("2 Jun", 6)])],
        [new ClientUsageRow("Acme Corp", 100, 120)],
        [new ExpiringLicenseRow("Initech", "Annual", new DateTimeOffset(Now).AddDays(4), 1200, 50000)],
        [],
        [new StatusCount("Active", 128), new StatusCount("Suspended", 6)],
        [new AlertModel("warning", "1 license(s) expire soon.")]);

    public static ClientDashboardModel ClientDashboard() => new(
        842, 1000, new DateTimeOffset(Now).AddDays(23), "Business", "842 credits available.",
        [new KpiModel("Face checks", "320", "i", null, true, null)], new OutcomeSplit(300, 15, 5),
        [new TrendSeries("Verify", [new TrendPoint("1 Jun", 3)])], [new RecognitionRow("0", new DateTimeOffset(Now), "Verify", "Matched", "12")],
        new ApiUsageModel(1200, 1.4, null), []);
}

/// <summary>bUnit setup for pages: a signed-in user with chosen permissions, all API clients faked, dialogs and snackbars ready.</summary>
public abstract class PageTestBase : UiTestBase
{
    protected PageTestBase()
    {
        Clients = new FakeClientsApi();
        Licensing = new FakeLicensingApi();
        Access = new FakeAccessApi();
        Dashboard = new FakeDashboardApi();
        Services.AddSingleton<IClientsApiClient>(Clients);
        Services.AddSingleton<ILicensingApiClient>(Licensing);
        Services.AddSingleton<IAccessApiClient>(Access);
        Services.AddSingleton<IDashboardApiClient>(Dashboard);
        Services.AddScoped<DashboardRangeState>();
        Services.AddSingleton<CurrentUserState>();
        Environment = new FakeEnv("Production");
        Services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(Environment);
        Services.AddSingleton<AntiforgeryStateProvider, FakeAntiforgeryState>();
    }

    protected FakeEnv Environment { get; }

    protected FakeClientsApi Clients { get; }

    protected FakeLicensingApi Licensing { get; }

    protected FakeAccessApi Access { get; }

    protected FakeDashboardApi Dashboard { get; }

    protected CurrentUserState User => Services.GetRequiredService<CurrentUserState>();

    protected void SignInAs(params string[] permissions) =>
        User.SignIn("Ada Admin", "ada@nexaverify.test", "SuperAdmin", null, permissions);

    protected void SignInAsEverything() => SignInAs(WebPermissions.SuperAdminDefaults.Append(WebPermissions.LicensesVerifyLedger).ToArray());

    /// <summary>Renders dialog + popover providers first (pages open dialogs through them).</summary>
    protected IRenderedComponent<MudDialogProvider> Providers() => RenderProviders();
}

/// <summary>Lets <c>&lt;AntiforgeryToken /&gt;</c> render in bUnit.</summary>
public sealed class FakeAntiforgeryState : AntiforgeryStateProvider
{
    public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("form-token-123", "__RequestVerificationToken");
}

public sealed class FakeEnv(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "t";

    public string ContentRootPath { get; set; } = "/";

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

public sealed class FakeAuthState : AuthenticationStateProvider
{
    public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity());

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));

    public static FakeAuthState For(PortalSession session) => new() { User = PortalPrincipalFactory.Create(session) };
}
