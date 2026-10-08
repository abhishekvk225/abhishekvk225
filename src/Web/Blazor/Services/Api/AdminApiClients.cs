using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Web.Services;

/// <summary>Builds query strings with proper escaping; null/empty values are dropped.</summary>
public static class ApiQuery
{
    public static string With(string path, params (string Name, object? Value)[] values)
    {
        var parts = values
            .Where(v => v.Value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Select(v => $"{v.Name}={Uri.EscapeDataString(Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture)!)}")
            .ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    public static string Paged(string path, PageRequest page, params (string Name, object? Value)[] extra) =>
        With(path, [("page", page.Page), ("pageSize", page.PageSize), ("sort", page.Sort), ("search", page.Search), .. extra]);
}

/// <summary>Platform-side client management (<c>/admin/clients</c>).</summary>
public interface IClientsApiClient
{
    Task<ApiResult<PagedResult<ClientListItemDto>>> ListAsync(PageRequest page, string? status, CancellationToken ct = default);

    Task<ApiResult<ClientDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken ct = default);

    Task<ApiResult<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct = default);

    Task<ApiResult<ClientDto>> ActivateAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<ClientDto>> DeactivateAsync(Guid id, string? reason, CancellationToken ct = default);

    Task<ApiResult<ClientDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default);

    Task<ApiResult<PagedResult<ClientUserDto>>> ListUsersAsync(Guid id, PageRequest page, CancellationToken ct = default);

    Task<ApiResult<bool>> ResetUserPasswordAsync(Guid clientId, Guid userId, CancellationToken ct = default);

    Task<ApiResult<PagedResult<AuditLogDto>>> GetActivityAsync(Guid id, ActivityQuery query, CancellationToken ct = default);

    Task<ApiResult<PagedResult<LoginHistoryDto>>> GetLoginsAsync(Guid id, ActivityQuery query, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<SettingDto>>> GetSettingsAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(Guid id, UpdateSettingsRequest request, CancellationToken ct = default);
}

public sealed class ClientsApiClient(IApiGateway api) : IClientsApiClient
{
    public Task<ApiResult<PagedResult<ClientListItemDto>>> ListAsync(PageRequest page, string? status, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<ClientListItemDto>>(ApiQuery.Paged("admin/clients", page, ("status", status)), ct);

    public Task<ApiResult<ClientDto>> GetAsync(Guid id, CancellationToken ct = default) => api.GetAsync<ClientDto>($"admin/clients/{id}", ct);

    public Task<ApiResult<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken ct = default) =>
        api.SendAsync<ClientDto>(HttpMethod.Post, "admin/clients", request, ct);

    public Task<ApiResult<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct = default) =>
        api.SendAsync<ClientDto>(HttpMethod.Put, $"admin/clients/{id}", request, ct);

    public Task<ApiResult<ClientDto>> ActivateAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<ClientDto>(HttpMethod.Post, $"admin/clients/{id}/activate", null, ct);

    public Task<ApiResult<ClientDto>> DeactivateAsync(Guid id, string? reason, CancellationToken ct = default) =>
        api.SendAsync<ClientDto>(HttpMethod.Post, $"admin/clients/{id}/deactivate", new ClientStatusRequest(reason), ct);

    public Task<ApiResult<ClientDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default) =>
        api.SendAsync<ClientDto>(HttpMethod.Post, $"admin/clients/{id}/suspend", new ClientStatusRequest(reason), ct);

    public Task<ApiResult<PagedResult<ClientUserDto>>> ListUsersAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<ClientUserDto>>(ApiQuery.Paged($"admin/clients/{id}/users", page), ct);

    public Task<ApiResult<bool>> ResetUserPasswordAsync(Guid clientId, Guid userId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"admin/clients/{clientId}/users/{userId}/reset-password", null, ct);

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetActivityAsync(Guid id, ActivityQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<AuditLogDto>>(Activity($"admin/clients/{id}/activity", query), ct);

    public Task<ApiResult<PagedResult<LoginHistoryDto>>> GetLoginsAsync(Guid id, ActivityQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<LoginHistoryDto>>(Activity($"admin/clients/{id}/logins", query), ct);

    public Task<ApiResult<IReadOnlyList<SettingDto>>> GetSettingsAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<SettingDto>>($"admin/clients/{id}/settings", ct);

    public Task<ApiResult<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(Guid id, UpdateSettingsRequest request, CancellationToken ct = default) =>
        api.SendAsync<IReadOnlyList<SettingDto>>(HttpMethod.Put, $"admin/clients/{id}/settings", request, ct);

    private static string Activity(string path, ActivityQuery q) =>
        ApiQuery.With(path, ("page", q.Page), ("pageSize", q.PageSize), ("from", q.From?.ToString("O")), ("to", q.To?.ToString("O")), ("action", q.Action), ("outcome", q.Outcome));
}

/// <summary>Licenses, plans, cost rules and ledger tools (<c>/admin/licenses</c>, <c>/admin/plans</c>, <c>/admin/cost-rules</c>).</summary>
public interface ILicensingApiClient
{
    Task<ApiResult<PagedResult<LicenseListItemDto>>> ListAsync(PageRequest page, string? status, Guid? clientId, int? expiringInDays, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> CreateAsync(Guid clientId, CreateLicenseRequest request, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> UpdateAsync(Guid id, UpdateLicenseRequest request, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> ActivateAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> DeactivateAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> RevokeAsync(Guid id, string? reason, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> RenewAsync(Guid id, RenewLicenseRequest request, CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> AdjustAsync(Guid id, AdjustLicenseRequest request, CancellationToken ct = default);

    Task<ApiResult<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken ct = default);

    Task<ApiResult<LicenseTransactionDto>> RefundAsync(long transactionId, RefundRequest request, CancellationToken ct = default);

    Task<ApiResult<LedgerVerificationDto>> VerifyLedgerAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<LedgerVerificationReportDto>> VerifyAllLedgersAsync(CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<PlanDto>>> ListPlansAsync(CancellationToken ct = default);

    Task<ApiResult<PlanDto>> CreatePlanAsync(SavePlanRequest request, CancellationToken ct = default);

    Task<ApiResult<PlanDto>> UpdatePlanAsync(Guid id, SavePlanRequest request, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<CostRuleDto>>> ListCostRulesAsync(CancellationToken ct = default);

    Task<ApiResult<CostRuleDto>> SetDefaultCostRuleAsync(SetCostRuleRequest request, CancellationToken ct = default);

    Task<ApiResult<CostRuleDto>> SetPlanCostRuleAsync(Guid planId, SetCostRuleRequest request, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<CostRuleDto>>> ListClientCostRulesAsync(Guid clientId, CancellationToken ct = default);

    Task<ApiResult<CostRuleDto>> SetClientCostRuleAsync(Guid clientId, SetCostRuleRequest request, CancellationToken ct = default);
}

public sealed class LicensingApiClient(IApiGateway api) : ILicensingApiClient
{
    public Task<ApiResult<PagedResult<LicenseListItemDto>>> ListAsync(PageRequest page, string? status, Guid? clientId, int? expiringInDays, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<LicenseListItemDto>>(
            ApiQuery.With("admin/licenses", ("page", page.Page), ("pageSize", page.PageSize), ("search", page.Search), ("status", status), ("clientId", clientId), ("expiringInDays", expiringInDays)), ct);

    public Task<ApiResult<LicenseDto>> GetAsync(Guid id, CancellationToken ct = default) => api.GetAsync<LicenseDto>($"admin/licenses/{id}", ct);

    public Task<ApiResult<LicenseDto>> CreateAsync(Guid clientId, CreateLicenseRequest request, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/clients/{clientId}/licenses", request, ct);

    public Task<ApiResult<LicenseDto>> UpdateAsync(Guid id, UpdateLicenseRequest request, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Put, $"admin/licenses/{id}", request, ct);

    public Task<ApiResult<LicenseDto>> ActivateAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/licenses/{id}/activate", null, ct);

    public Task<ApiResult<LicenseDto>> DeactivateAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/licenses/{id}/deactivate", null, ct);

    public Task<ApiResult<LicenseDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/licenses/{id}/suspend", new LicenseReasonRequest(reason), ct);

    public Task<ApiResult<LicenseDto>> RevokeAsync(Guid id, string? reason, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/licenses/{id}/revoke", new LicenseReasonRequest(reason), ct);

    public Task<ApiResult<LicenseDto>> RenewAsync(Guid id, RenewLicenseRequest request, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/licenses/{id}/renew", request, ct);

    public Task<ApiResult<LicenseDto>> AdjustAsync(Guid id, AdjustLicenseRequest request, CancellationToken ct = default) =>
        api.SendAsync<LicenseDto>(HttpMethod.Post, $"admin/licenses/{id}/adjust", request, ct);

    public Task<ApiResult<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<LicenseTransactionDto>>(ApiQuery.With($"admin/licenses/{id}/transactions", ("page", page.Page), ("pageSize", page.PageSize), ("sort", page.Sort)), ct);

    public Task<ApiResult<LicenseTransactionDto>> RefundAsync(long transactionId, RefundRequest request, CancellationToken ct = default) =>
        api.SendAsync<LicenseTransactionDto>(HttpMethod.Post, $"admin/transactions/{transactionId}/refund", request, ct);

    public Task<ApiResult<LedgerVerificationDto>> VerifyLedgerAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<LedgerVerificationDto>($"admin/licenses/{id}/verify-ledger", ct);

    public Task<ApiResult<LedgerVerificationReportDto>> VerifyAllLedgersAsync(CancellationToken ct = default) =>
        api.SendAsync<LedgerVerificationReportDto>(HttpMethod.Post, "admin/licensing/verify-ledger", null, ct, new ApiCallOptions { LongRunning = true });

    public Task<ApiResult<IReadOnlyList<PlanDto>>> ListPlansAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<PlanDto>>("admin/plans", ct);

    public Task<ApiResult<PlanDto>> CreatePlanAsync(SavePlanRequest request, CancellationToken ct = default) =>
        api.SendAsync<PlanDto>(HttpMethod.Post, "admin/plans", request, ct);

    public Task<ApiResult<PlanDto>> UpdatePlanAsync(Guid id, SavePlanRequest request, CancellationToken ct = default) =>
        api.SendAsync<PlanDto>(HttpMethod.Put, $"admin/plans/{id}", request, ct);

    public Task<ApiResult<IReadOnlyList<CostRuleDto>>> ListCostRulesAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<CostRuleDto>>("admin/cost-rules", ct);

    public Task<ApiResult<CostRuleDto>> SetDefaultCostRuleAsync(SetCostRuleRequest request, CancellationToken ct = default) =>
        api.SendAsync<CostRuleDto>(HttpMethod.Put, "admin/cost-rules/default", request, ct);

    public Task<ApiResult<CostRuleDto>> SetPlanCostRuleAsync(Guid planId, SetCostRuleRequest request, CancellationToken ct = default) =>
        api.SendAsync<CostRuleDto>(HttpMethod.Put, $"admin/cost-rules/plans/{planId}", request, ct);

    public Task<ApiResult<IReadOnlyList<CostRuleDto>>> ListClientCostRulesAsync(Guid clientId, CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<CostRuleDto>>($"admin/cost-rules/clients/{clientId}", ct);

    public Task<ApiResult<CostRuleDto>> SetClientCostRuleAsync(Guid clientId, SetCostRuleRequest request, CancellationToken ct = default) =>
        api.SendAsync<CostRuleDto>(HttpMethod.Put, $"admin/cost-rules/clients/{clientId}", request, ct);
}

/// <summary>Platform staff accounts, roles and the permission catalogue.</summary>
public interface IAccessApiClient
{
    Task<ApiResult<PagedResult<PlatformUserDto>>> ListUsersAsync(PageRequest page, CancellationToken ct = default);

    Task<ApiResult<PlatformUserDto>> CreateUserAsync(CreatePlatformUserRequest request, CancellationToken ct = default);

    Task<ApiResult<PlatformUserDto>> UpdateUserAsync(Guid id, UpdatePlatformUserRequest request, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken ct = default);

    Task<ApiResult<RoleDto>> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default);

    Task<ApiResult<RoleDto>> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<PermissionDto>>> ListPermissionsAsync(CancellationToken ct = default);
}

public sealed class AccessApiClient(IApiGateway api) : IAccessApiClient
{
    public Task<ApiResult<PagedResult<PlatformUserDto>>> ListUsersAsync(PageRequest page, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<PlatformUserDto>>(ApiQuery.Paged("admin/users", page), ct);

    public Task<ApiResult<PlatformUserDto>> CreateUserAsync(CreatePlatformUserRequest request, CancellationToken ct = default) =>
        api.SendAsync<PlatformUserDto>(HttpMethod.Post, "admin/users", request, ct);

    public Task<ApiResult<PlatformUserDto>> UpdateUserAsync(Guid id, UpdatePlatformUserRequest request, CancellationToken ct = default) =>
        api.SendAsync<PlatformUserDto>(HttpMethod.Put, $"admin/users/{id}", request, ct);

    public Task<ApiResult<IReadOnlyList<RoleDto>>> ListRolesAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<RoleDto>>("admin/roles", ct);

    public Task<ApiResult<RoleDto>> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default) =>
        api.SendAsync<RoleDto>(HttpMethod.Post, "admin/roles", request, ct);

    public Task<ApiResult<RoleDto>> UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default) =>
        api.SendAsync<RoleDto>(HttpMethod.Put, $"admin/roles/{id}", request, ct);

    public Task<ApiResult<IReadOnlyList<PermissionDto>>> ListPermissionsAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<PermissionDto>>("admin/permissions", ct);
}
