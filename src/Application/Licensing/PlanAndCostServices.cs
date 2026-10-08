using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

public interface IPlanService
{
    Task<Result<IReadOnlyList<PlanDto>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<PlanDto>> CreateAsync(SavePlanRequest request, CancellationToken cancellationToken);

    Task<Result<PlanDto>> UpdateAsync(Guid id, SavePlanRequest request, CancellationToken cancellationToken);
}

public sealed class PlanService : IPlanService
{
    private readonly IPlanRepository _plans;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;

    public PlanService(IPlanRepository plans, IAuditService audit, IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _audit = audit;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<IReadOnlyList<PlanDto>>> ListAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<PlanDto>>.Success((await _plans.ListAsync(cancellationToken)).Select(p => p.ToDto()).ToList());

    public async Task<Result<PlanDto>> CreateAsync(SavePlanRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await _plans.CodeExistsAsync(code, null, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Conflict, "A plan with this code already exists.");
        }

        Plan plan;
        try
        {
            plan = Plan.Create(code, request.Name, request.DefaultCredits, request.DefaultDurationDays);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        Apply(plan, request);
        _plans.Add(plan);
        _audit.Record(new AuditEntry("plan.created", nameof(Plan), plan.Id.ToString(), NewValues: new { plan.Code, plan.Name, plan.DefaultCredits }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return plan.ToDto();
    }

    public async Task<Result<PlanDto>> UpdateAsync(Guid id, SavePlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await _plans.GetByIdAsync(id, cancellationToken);
        if (plan is null)
        {
            return Error.NotFound();
        }

        // The code is the plan's stable identity (it appears in contracts and reports) and is not editable.
        if (!string.Equals(plan.Code, request.Code.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("A plan's code cannot be changed.", new Dictionary<string, string[]> { ["code"] = ["Cannot be changed."] });
        }

        var before = new { plan.Name, plan.DefaultCredits, plan.DefaultDurationDays, plan.IsActive };
        plan.Name = request.Name.Trim();
        Apply(plan, request);
        _audit.Record(new AuditEntry("plan.updated", nameof(Plan), plan.Id.ToString(), OldValues: before,
            NewValues: new { plan.Name, plan.DefaultCredits, plan.DefaultDurationDays, plan.IsActive }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return plan.ToDto();
    }

    private static void Apply(Plan plan, SavePlanRequest r)
    {
        plan.Name = r.Name.Trim();
        plan.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        plan.DefaultCredits = r.DefaultCredits;
        plan.DefaultDurationDays = r.DefaultDurationDays;
        plan.RateLimitPerMinute = r.RateLimitPerMinute;
        plan.DailyQuota = r.DailyQuota;
        plan.MaxFaceProfiles = r.MaxFaceProfiles;
        plan.MaxApiKeys = r.MaxApiKeys;
        plan.MaxUsers = r.MaxUsers;
        plan.IsActive = r.IsActive;
    }
}

public interface ICostRuleService
{
    Task<Result<IReadOnlyList<CostRuleDto>>> ListPlatformAsync(CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<CostRuleDto>>> ListClientAsync(Guid clientId, CancellationToken cancellationToken);

    /// <summary>Sets the platform default (<paramref name="planId"/> null) or a plan's cost: closes the current rule and starts a new one.</summary>
    Task<Result<CostRuleDto>> SetPlatformRuleAsync(Guid? planId, SetCostRuleRequest request, CancellationToken cancellationToken);

    Task<Result<CostRuleDto>> SetClientRuleAsync(Guid clientId, SetCostRuleRequest request, CancellationToken cancellationToken);
}

public sealed class CostRuleService : ICostRuleService
{
    private readonly ICostRuleRepository _rules;
    private readonly IPlanRepository _plans;
    private readonly IClientRepository _clients;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public CostRuleService(ICostRuleRepository rules, IPlanRepository plans, IClientRepository clients, IAuditService audit, IUnitOfWork unitOfWork, TimeProvider time)
    {
        _rules = rules;
        _plans = plans;
        _clients = clients;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<Result<IReadOnlyList<CostRuleDto>>> ListPlatformAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<CostRuleDto>>.Success((await _rules.ListPlatformRulesAsync(cancellationToken)).Select(Map).ToList());

    public async Task<Result<IReadOnlyList<CostRuleDto>>> ListClientAsync(Guid clientId, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(clientId, cancellationToken) is not { IsSystem: false })
        {
            return Error.NotFound();
        }

        return Result<IReadOnlyList<CostRuleDto>>.Success((await _rules.ListClientRulesAsync(clientId, cancellationToken)).Select(Map).ToList());
    }

    public async Task<Result<CostRuleDto>> SetPlatformRuleAsync(Guid? planId, SetCostRuleRequest request, CancellationToken cancellationToken)
    {
        if (planId is { } pid && await _plans.GetByIdAsync(pid, cancellationToken) is null)
        {
            return Error.NotFound("The plan was not found.");
        }

        if (!TryParse(request, out var operation, out var policy, out var error))
        {
            return error!;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var from = request.EffectiveFrom is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : now;
        CostRule rule;
        try
        {
            rule = CostRule.Create(planId, operation, request.Credits, policy, from);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        foreach (var open in (await _rules.ListPlatformRulesAsync(cancellationToken)).Where(r => r.PlanId == planId && r.Operation == operation && r.EffectiveTo is null && r.EffectiveFrom < from))
        {
            open.CloseAt(from);
        }

        _rules.Add(rule);
        _audit.Record(new AuditEntry("costrule.set", nameof(CostRule), rule.Id.ToString(),
            NewValues: new { PlanId = planId, Operation = operation.ToString(), request.Credits, Policy = policy.ToString() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Map(rule);
    }

    public async Task<Result<CostRuleDto>> SetClientRuleAsync(Guid clientId, SetCostRuleRequest request, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(clientId, cancellationToken) is not { IsSystem: false })
        {
            return Error.NotFound();
        }

        if (!TryParse(request, out var operation, out var policy, out var error))
        {
            return error!;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var from = request.EffectiveFrom is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : now;
        ClientCostRule rule;
        try
        {
            rule = ClientCostRule.Create(clientId, operation, request.Credits, policy, from);
        }
        catch (DomainException ex)
        {
            return ex.ToError();
        }

        foreach (var open in (await _rules.ListClientRulesAsync(clientId, cancellationToken)).Where(r => r.Operation == operation && r.EffectiveTo is null && r.EffectiveFrom < from))
        {
            open.CloseAt(from);
        }

        _rules.Add(rule);
        _audit.Record(new AuditEntry("costrule.client_set", nameof(ClientCostRule), rule.Id.ToString(), clientId,
            NewValues: new { Operation = operation.ToString(), request.Credits, Policy = policy.ToString() }));
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Map(rule);
    }

    private static bool TryParse(SetCostRuleRequest r, out MeteredOperation operation, out ChargePolicy policy, out Error? error)
    {
        policy = default;
        error = null;
        if (!Enum.TryParse(r.Operation, ignoreCase: true, out operation) || !Enum.IsDefined(operation))
        {
            error = Error.Validation("Unknown operation.", new Dictionary<string, string[]> { ["operation"] = ["Unknown operation."] });
            return false;
        }

        if (!Enum.TryParse(r.ChargePolicy, ignoreCase: true, out policy) || !Enum.IsDefined(policy))
        {
            error = Error.Validation("Unknown charge policy.", new Dictionary<string, string[]> { ["chargePolicy"] = ["Unknown charge policy."] });
            return false;
        }

        return true;
    }

    private static CostRuleDto Map(CostRule r) => new(r.Id, r.PlanId is null ? "PlatformDefault" : "Plan", null, r.PlanId, r.Operation.ToString(), r.Credits, r.ChargePolicy.ToString(), r.EffectiveFrom, r.EffectiveTo);

    private static CostRuleDto Map(ClientCostRule r) => new(r.Id, "Client", r.ClientId, null, r.Operation.ToString(), r.Credits, r.ChargePolicy.ToString(), r.EffectiveFrom, r.EffectiveTo);
}

public interface IClientLicenseService
{
    /// <summary>The caller's own credits as one plain-language picture (client is taken from the credential).</summary>
    Task<Result<LicenseSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken);

    Task<Result<LicenseDto>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken cancellationToken);
}

public sealed class ClientLicenseService : IClientLicenseService
{
    private readonly ILicenseRepository _licenses;
    private readonly ILedgerRepository _ledger;
    private readonly Tenancy.IClientSettingsService _settings;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _time;

    public ClientLicenseService(ILicenseRepository licenses, ILedgerRepository ledger, Tenancy.IClientSettingsService settings, ICurrentUser currentUser, TimeProvider time)
    {
        _licenses = licenses;
        _ledger = ledger;
        _settings = settings;
        _currentUser = currentUser;
        _time = time;
    }

    public async Task<Result<LicenseSummaryDto>> GetSummaryAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client accounts have a license.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var rows = await _licenses.ListForClientAsync(clientId, cancellationToken);
        var settings = await _settings.GetEffectiveAsync(clientId, cancellationToken);
        var lowPercent = settings.Int(Contracts.Tenancy.SettingKeys.Notify.LowBalancePercent);
        var expiryDays = settings.Int(Contracts.Tenancy.SettingKeys.Notify.ExpiryDaysBefore);

        var usable = rows.Where(r => r.License.Status == LicenseStatus.Active && r.License.StartsAt <= now && now < r.License.ExpiresAt).ToList();
        var remaining = usable.Sum(r => r.License.Remaining);
        var total = usable.Sum(r => r.License.TotalCredits);
        var consumed = usable.Sum(r => r.License.ConsumedCredits);
        var percent = total == 0 ? 0 : (int)Math.Floor(100.0 * remaining / total);
        var next = usable.Where(r => r.License.Remaining > 0).Select(r => (DateTime?)r.License.ExpiresAt).Min();
        int? daysUntil = next is { } n ? Math.Max(0, (int)Math.Ceiling((n - now).TotalDays)) : null;

        var (health, message) =
            usable.Count == 0 && rows.Any(r => r.License.Status == LicenseStatus.Suspended) ? ("Suspended", "Your license is suspended. Please contact support.")
            : usable.Count == 0 && rows.Any(r => r.License.EffectiveStatus(now) == LicenseStatus.Expired) ? ("Expired", "Your license has expired. Please renew it.")
            : usable.Count == 0 ? ("None", "There is no active license for this account.")
            : remaining == 0 ? ("Depleted", "Your credits are used up. Please top up your license.")
            : percent <= lowPercent ? ("Low", $"Only {remaining} credits remain ({percent}%).")
            : daysUntil is { } d && d <= expiryDays ? ("Expiring", $"Your license expires in {d} day(s).")
            : ("Healthy", $"{remaining} credits available.");

        return new LicenseSummaryDto(health, message, remaining, total, consumed, percent, next, daysUntil, usable.Count, rows.Select(r => r.ToListItem(now)).ToList());
    }

    public async Task<Result<LicenseDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _licenses.GetRowAsync(id, cancellationToken);
        return row is null || row.License.ClientId != _currentUser.ClientId ? Error.NotFound() : row.ToDto(_time.GetUtcNow().UtcDateTime);
    }

    public async Task<Result<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken cancellationToken)
    {
        var license = await _licenses.GetByIdAsync(id, cancellationToken);
        if (license is null || license.ClientId != _currentUser.ClientId)
        {
            return Error.NotFound();
        }

        var paging = page.Normalize();
        var (items, total) = await _ledger.ListAsync(id, paging.Skip, paging.PageSize, cancellationToken);
        return new PagedResult<LicenseTransactionDto>(items.Select(t => t.ToDto()).ToList(), paging.Page, paging.PageSize, total);
    }
}

/// <summary>Expiry housekeeping: flips lapsed licenses to Expired and writes their unused credits off, one license per transaction.</summary>
public interface ILicenseExpiryProcessor
{
    Task<int> ProcessAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed class LicenseExpiryProcessor : ILicenseExpiryProcessor
{
    private readonly ILicenseRepository _licenses;
    private readonly LedgerWriter _writer;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public LicenseExpiryProcessor(ILicenseRepository licenses, LedgerWriter writer, IAuditService audit, IUnitOfWork unitOfWork, TimeProvider time)
    {
        _licenses = licenses;
        _writer = writer;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _time = time;
    }

    public async Task<int> ProcessAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var processed = 0;
        foreach (var id in await _licenses.GetDueForExpiryAsync(now, batchSize, cancellationToken))
        {
            var license = await _licenses.GetByIdAsync(id, cancellationToken);
            if (license is null)
            {
                continue;
            }

            try
            {
                await _unitOfWork.ExecuteInTransactionAsync(
                    async ct =>
                    {
                        var before = license.Remaining;
                        if (license.Expire(now) is not { } written)
                        {
                            return false;
                        }

                        await _unitOfWork.SaveChangesAsync(ct); // row lock + version check before the ledger tail is read
                        if (written > 0)
                        {
                            await _writer.AppendAsync(license, LedgerEntryType.ExpiryWriteOff, -written, before, ct, reason: "License expired");
                        }

                        _audit.Record(new AuditEntry("license.expired", nameof(License), license.Id.ToString(), license.ClientId, NewValues: new { WrittenOff = written }));
                        await _unitOfWork.SaveChangesAsync(ct);
                        return true;
                    },
                    cancellationToken);
                processed++;
            }
            catch (ConcurrencyConflictException)
            {
                // A concurrent charge/renewal touched it; the next sweep picks it up again if it is still due.
            }
        }

        return processed;
    }
}
