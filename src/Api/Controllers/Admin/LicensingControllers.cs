using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;

namespace NexaVerify.Api.Controllers.Admin;

/// <summary>Platform-side license administration and the immutable credit ledger.</summary>
[Route("api/v1/admin")]
public sealed class LicensesController : ApiControllerBase
{
    private readonly ILicenseService _licenses;

    public LicensesController(ILicenseService licenses)
    {
        _licenses = licenses;
    }

    [HttpGet("licenses")]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> List([FromQuery] LicenseListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.ListAsync(query, cancellationToken));

    [HttpGet("clients/{clientId:guid}/licenses")]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> ListForClient(Guid clientId, [FromQuery] LicenseListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.ListAsync(query with { ClientId = clientId }, cancellationToken));

    [HttpPost("clients/{clientId:guid}/licenses")]
    [HasPermission(Permissions.Licenses.Create)]
    [ProducesResponseType<LicenseDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid clientId, CreateLicenseRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.CreateAsync(clientId, request, cancellationToken), l => Created($"/api/v1/admin/licenses/{l.Id}", l));

    [HttpGet("licenses/{id:guid}")]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.GetAsync(id, cancellationToken));

    [HttpPut("licenses/{id:guid}")]
    [HasPermission(Permissions.Licenses.Update)]
    public async Task<IActionResult> Update(Guid id, UpdateLicenseRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.UpdateAsync(id, request, cancellationToken));

    [HttpPost("licenses/{id:guid}/activate")]
    [HasPermission(Permissions.Licenses.ManageStatus)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.ActivateAsync(id, cancellationToken));

    [HttpPost("licenses/{id:guid}/deactivate")]
    [HasPermission(Permissions.Licenses.ManageStatus)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.DeactivateAsync(id, cancellationToken));

    [HttpPost("licenses/{id:guid}/suspend")]
    [HasPermission(Permissions.Licenses.ManageStatus)]
    public async Task<IActionResult> Suspend(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.SuspendAsync(id, request, cancellationToken));

    [HttpPost("licenses/{id:guid}/revoke")]
    [HasPermission(Permissions.Licenses.ManageStatus)]
    public async Task<IActionResult> Revoke(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.RevokeAsync(id, request, cancellationToken));

    [HttpPost("licenses/{id:guid}/renew")]
    [HasPermission(Permissions.Licenses.Renew)]
    public async Task<IActionResult> Renew(Guid id, RenewLicenseRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.RenewAsync(id, request, cancellationToken));

    [HttpPost("licenses/{id:guid}/adjust")]
    [HasPermission(Permissions.Licenses.Adjust)]
    public async Task<IActionResult> Adjust(Guid id, AdjustLicenseRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.AdjustAsync(id, request, cancellationToken));

    [HttpGet("licenses/{id:guid}/transactions")]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> Transactions(Guid id, [FromQuery] PageRequest query, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.GetTransactionsAsync(id, query, cancellationToken));

    [HttpGet("licenses/{id:guid}/verify-ledger")]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> VerifyLedger(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.VerifyLedgerAsync(id, cancellationToken));

    [HttpPost("transactions/{transactionId:long}/refund")]
    [HasPermission(Permissions.Licenses.Adjust)]
    public async Task<IActionResult> Refund(long transactionId, RefundRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _licenses.RefundAsync(transactionId, request, cancellationToken));
}

[Route("api/v1/admin/plans")]
public sealed class PlansController : ApiControllerBase
{
    private readonly IPlanService _plans;

    public PlansController(IPlanService plans)
    {
        _plans = plans;
    }

    [HttpGet]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        ToActionResult(await _plans.ListAsync(cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.Plans.Manage)]
    [ProducesResponseType<PlanDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SavePlanRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _plans.CreateAsync(request, cancellationToken), p => Created($"/api/v1/admin/plans/{p.Id}", p));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Plans.Manage)]
    public async Task<IActionResult> Update(Guid id, SavePlanRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _plans.UpdateAsync(id, request, cancellationToken));
}

[Route("api/v1/admin/cost-rules")]
public sealed class CostRulesController : ApiControllerBase
{
    private readonly ICostRuleService _rules;

    public CostRulesController(ICostRuleService rules)
    {
        _rules = rules;
    }

    [HttpGet]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> ListPlatform(CancellationToken cancellationToken) =>
        ToActionResult(await _rules.ListPlatformAsync(cancellationToken));

    [HttpPut("default")]
    [HasPermission(Permissions.Licenses.CostRules)]
    public async Task<IActionResult> SetDefault(SetCostRuleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _rules.SetPlatformRuleAsync(null, request, cancellationToken));

    [HttpPut("plans/{planId:guid}")]
    [HasPermission(Permissions.Licenses.CostRules)]
    public async Task<IActionResult> SetPlan(Guid planId, SetCostRuleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _rules.SetPlatformRuleAsync(planId, request, cancellationToken));

    [HttpGet("clients/{clientId:guid}")]
    [HasPermission(Permissions.Licenses.Read)]
    public async Task<IActionResult> ListClient(Guid clientId, CancellationToken cancellationToken) =>
        ToActionResult(await _rules.ListClientAsync(clientId, cancellationToken));

    [HttpPut("clients/{clientId:guid}")]
    [HasPermission(Permissions.Licenses.CostRules)]
    public async Task<IActionResult> SetClient(Guid clientId, SetCostRuleRequest request, CancellationToken cancellationToken) =>
        ToActionResult(await _rules.SetClientRuleAsync(clientId, request, cancellationToken));
}
