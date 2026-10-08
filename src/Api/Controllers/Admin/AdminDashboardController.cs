using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Http;
using NexaVerify.Application.Dashboards;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;

namespace NexaVerify.Api.Controllers.Admin;

/// <summary>Platform-wide aggregates, the platform usage export and the on-demand ledger tamper check.</summary>
[Route("api/v1/admin")]
public sealed class AdminDashboardController : ApiControllerBase
{
    private readonly IAdminDashboardService _dashboard;
    private readonly IUsageReportService _reports;
    private readonly ILedgerVerificationService _verification;

    public AdminDashboardController(IAdminDashboardService dashboard, IUsageReportService reports, ILedgerVerificationService verification)
    {
        _dashboard = dashboard;
        _reports = reports;
        _verification = verification;
    }

    /// <summary>Clients and licenses by status, expiring and low-balance licenses, credits per day, top clients, API health, webhook health. Aggregates only.</summary>
    [HttpGet("dashboard")]
    [HasPermission(Permissions.Dashboard.Admin)]
    [ProducesResponseType<AdminDashboardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard([FromQuery] DashboardQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _dashboard.GetAsync(query, cancellationToken));

    /// <summary>Billed usage per day, client and operation as CSV (at most 92 days), neutralised against CSV injection and audit-logged.</summary>
    [HttpGet("reports/usage.csv")]
    [HasPermission(Permissions.Reports.Read)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UsageCsv([FromQuery] UsageReportQuery query, CancellationToken cancellationToken)
    {
        var result = await _reports.ExportAdminAsync(query, cancellationToken);
        return result.IsSuccess ? new CsvStreamResult(result.Value) : ProblemFor(result.Error!);
    }

    /// <summary>Recomputes every license's hash chain and balance; returns the first broken row of each license that fails (409 if a check is already running).</summary>
    [HttpPost("licensing/verify-ledger")]
    [HasPermission(Permissions.Licenses.VerifyLedger)]
    [ProducesResponseType<LedgerVerificationReportDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyLedger(CancellationToken cancellationToken) =>
        ToActionResult(await _verification.VerifyAllAsync(cancellationToken));
}
