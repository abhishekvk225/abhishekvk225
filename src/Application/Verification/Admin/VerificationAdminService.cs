using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Dashboards;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Admin;

public interface IVerificationAdminService
{
    /// <summary>All modules including Upcoming rows, effective status, provider configuration (booleans and ids only) and resolved platform costs.</summary>
    Task<Result<IReadOnlyList<AdminVerificationModuleDto>>> ListModulesAsync(CancellationToken cancellationToken);

    /// <summary>Switches an Available module on or off. Unknown and Upcoming keys are 404. Audited with the reason.</summary>
    Task<Result<AdminVerificationModuleDto>> UpdateModuleAsync(string key, UpdateVerificationModuleRequest request, CancellationToken cancellationToken);

    Task<Result<VerificationStatsDto>> GetStatsAsync(VerificationStatsQuery query, CancellationToken cancellationToken);

    Task<Result<UsageReportStream>> ExportStatsAsync(VerificationStatsQuery query, CancellationToken cancellationToken);

    /// <summary>Metadata only: never documents, extracted values, masked identifiers or checks.</summary>
    Task<Result<PagedResult<AdminVerificationItemDto>>> ListClientRequestsAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken cancellationToken);

    /// <summary>Metadata only. Each view is audited (<c>verification.admin_viewed</c>).</summary>
    Task<Result<AdminVerificationDetailDto>> GetClientRequestAsync(Guid clientId, Guid requestId, CancellationToken cancellationToken);

    Task<Result<PagedResult<AdminVerificationAuditItemDto>>> ListAuditAsync(AdminVerificationAuditQuery query, CancellationToken cancellationToken);
}

public sealed class VerificationAdminService : IVerificationAdminService
{
    private const int MaxPageSize = 100;

    private readonly ICurrentUser _user;
    private readonly IVerificationModuleRegistry _registry;
    private readonly IVerificationModuleSettings _settings;
    private readonly IVerificationAdminQueries _queries;
    private readonly IVerificationProviderCatalog _providers;
    private readonly ICostRuleService _costs;
    private readonly IClientRepository _clients;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public VerificationAdminService(
        ICurrentUser user,
        IVerificationModuleRegistry registry,
        IVerificationModuleSettings settings,
        IVerificationAdminQueries queries,
        IVerificationProviderCatalog providers,
        ICostRuleService costs,
        IClientRepository clients,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        TimeProvider time)
    {
        _user = user;
        _registry = registry;
        _settings = settings;
        _queries = queries;
        _providers = providers;
        _costs = costs;
        _clients = clients;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<IReadOnlyList<AdminVerificationModuleDto>>> ListModulesAsync(CancellationToken cancellationToken)
    {
        var settings = (await _settings.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(s => s.ModuleKey, StringComparer.OrdinalIgnoreCase);
        var outcomes = (await _queries.GetProviderOutcomesAsync(Now.AddHours(-24), cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.ProviderId, o => o.Counts, StringComparer.OrdinalIgnoreCase);
        var prices = await _costs.ListPlatformAsync(cancellationToken).ConfigureAwait(false);
        var platformPrices = prices.IsSuccess
            ? prices.Value.Where(p => p.PlanId is null && p.EffectiveTo is null).GroupBy(p => p.Operation).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.EffectiveFrom).First())
            : [];

        var result = new List<AdminVerificationModuleDto>();
        foreach (var d in _registry.All)
        {
            if (d.Status == ModuleStatus.Upcoming)
            {
                result.Add(new AdminVerificationModuleDto(d.Key, d.DisplayName, d.Description, VerificationModuleStatuses.Upcoming, false, null, d.DisplayOrder, [], [], null, null));
                continue;
            }

            var effective = await _registry.GetEffectiveStatusAsync(d.Key, cancellationToken).ConfigureAwait(false);
            settings.TryGetValue(d.Key, out var setting);
            var providers = _providers.Describe().Where(p => p.Type == d.Type).Select(p =>
                new VerificationProviderStatusDto(p.ProviderId, p.Type.ToString(), p.Configured, outcomes.GetValueOrDefault(p.ProviderId) ?? new ProviderOutcomeCountsDto(0, 0, 0, 0, 0))).ToList();
            var costs = d.CostOperations.Select(o => platformPrices.TryGetValue(o.ToString(), out var price)
                ? new ModuleCostDto(o.ToString(), price.Credits, price.ChargePolicy)
                : new ModuleCostDto(o.ToString(), CostRuleResolver.Fallback(o).Credits, CostRuleResolver.Fallback(o).Policy.ToString())).ToList();
            result.Add(new AdminVerificationModuleDto(
                d.Key, d.DisplayName, d.Description, effective.ToString(), effective == ModuleStatus.Available, setting?.IsEnabled ?? true, d.DisplayOrder,
                providers, costs, setting?.UpdatedAt, setting is null ? null : Convert.ToBase64String(setting.RowVersion)));
        }

        return result;
    }

    public async Task<Result<AdminVerificationModuleDto>> UpdateModuleAsync(string key, UpdateVerificationModuleRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Find(key);
        if (descriptor is null || descriptor.Status != ModuleStatus.Available)
        {
            return Error.NotFound("The module was not found.");
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(request.RowVersion);
        }
        catch (FormatException)
        {
            return Error.Validation("The row version is not valid.", new Dictionary<string, string[]> { ["rowVersion"] = ["Not valid."] });
        }

        var now = Now;
        var setting = await _settings.GetAsync(descriptor.Key, cancellationToken).ConfigureAwait(false);
        var wasEnabled = setting?.IsEnabled ?? true;
        if (setting is null)
        {
            setting = VerificationModuleSetting.Create(descriptor.Key, request.IsEnabled, now, _user.ActorId);
            _settings.Add(setting);
        }
        else
        {
            _settings.SetExpectedVersion(setting, expected);
            setting.SetEnabled(request.IsEnabled, now, _user.ActorId);
        }

        _audit.Record(new AuditEntry(
            request.IsEnabled ? VerificationAuditActions.ModuleEnabled : VerificationAuditActions.ModuleDisabled, VerificationAuditActions.ModuleEntityType,
            descriptor.Key, PlatformTenant.ClientId, OldValues: new { isEnabled = wasEnabled }, NewValues: new { isEnabled = request.IsEnabled, reason = request.Reason.Trim() }));
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            _unitOfWork.ClearTracked();
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "The module was changed by someone else. Reload and try again.");
        }

        var list = await ListModulesAsync(cancellationToken).ConfigureAwait(false);
        return list.IsFailure ? list.Error! : list.Value.First(m => string.Equals(m.Key, descriptor.Key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Result<VerificationStatsDto>> GetStatsAsync(VerificationStatsQuery query, CancellationToken cancellationToken)
    {
        if (Validate(query) is { } error)
        {
            return error;
        }

        return await _queries.GetStatsAsync(query, Now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<UsageReportStream>> ExportStatsAsync(VerificationStatsQuery query, CancellationToken cancellationToken)
    {
        if (Validate(query) is { } error)
        {
            return error;
        }

        var now = Now;
        var series = await _queries.GetTimeSeriesAsync(query, cancellationToken).ConfigureAwait(false);
        _audit.Record(new AuditEntry("verification.stats_exported", VerificationAuditActions.EntityType, "stats", PlatformTenant.ClientId, NewValues: new { rows = series.Count }));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new UsageReportStream($"verification-stats-{now:yyyyMMdd}.csv", Lines(series));
    }

    private static async IAsyncEnumerable<string> Lines(IReadOnlyList<VerificationTimePoint> series, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return CsvFormat.Line("date", "type", "total", "completed", "reviewRequired", "failed", "creditsCharged");
        foreach (var p in series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CsvFormat.Line(p.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), p.Type.ToString(), p.Total, p.Completed, p.ReviewRequired, p.Failed, p.CreditsCharged);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<Result<PagedResult<AdminVerificationItemDto>>> ListClientRequestsAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(clientId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Error.NotFound("The client was not found.");
        }

        var page = Math.Clamp(query.Page, 1, PageRequest.MaxPage);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var (items, total) = await _queries.ListForClientAsync(clientId, query with { Page = page, PageSize = size }, cancellationToken).ConfigureAwait(false);
        return new PagedResult<AdminVerificationItemDto>(items, page, size, total);
    }

    public async Task<Result<AdminVerificationDetailDto>> GetClientRequestAsync(Guid clientId, Guid requestId, CancellationToken cancellationToken)
    {
        var detail = await _queries.GetDetailAsync(clientId, requestId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return Error.NotFound("The verification was not found.");
        }

        _audit.Record(new AuditEntry(
            VerificationAuditActions.AdminViewed, VerificationAuditActions.EntityType, requestId.ToString(), PlatformTenant.ClientId,
            NewValues: new { clientId, requestId }));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return detail;
    }

    public async Task<Result<PagedResult<AdminVerificationAuditItemDto>>> ListAuditAsync(AdminVerificationAuditQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Clamp(query.Page, 1, PageRequest.MaxPage);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var (items, total) = await _queries.ListPlatformAuditAsync(query.From, query.To, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);
        return new PagedResult<AdminVerificationAuditItemDto>(
            items.Select(a => new AdminVerificationAuditItemDto(a.Id, a.OccurredAt, a.Action, a.EntityType, a.EntityId, a.ActorType.ToString(), a.ActorId, a.CorrelationId)).ToList(),
            page, size, total);
    }

    private static Error? Validate(VerificationStatsQuery query) =>
        query.From is { } f && query.To is { } t && f > t
            ? Error.Validation("The period is not valid.", new Dictionary<string, string[]> { ["from"] = ["from must not be after to."] })
            : null;
}
