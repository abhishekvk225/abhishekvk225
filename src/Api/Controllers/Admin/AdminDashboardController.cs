using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;
using NexaVerify.Application.Api;
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
    [Throttle(ThrottlePolicies.Dashboards)]
    [ProducesResponseType<AdminDashboardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard([FromQuery] DashboardQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _dashboard.GetAsync(query, cancellationToken));

    /// <summary>Billed usage per day, client and operation as CSV (at most 92 days), neutralised against CSV injection and audit-logged.</summary>
    [HttpGet("reports/usage.csv")]
    [HasPermission(Permissions.Reports.Read)]
    [Throttle(ThrottlePolicies.Exports)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UsageCsv([FromQuery] UsageReportQuery query, CancellationToken cancellationToken)
    {
        var result = await _reports.ExportAdminAsync(query, cancellationToken);
        return result.IsSuccess ? new CsvStreamResult(result.Value) : ProblemFor(result.Error!);
    }

    /// <summary>
    /// Starts a ledger tamper check in the background and returns 202 with a run id at once (409 if a check is already running on any
    /// node). The optional body limits the check to one license. Poll the run with <c>GET .../runs/{id}</c>.
    /// </summary>
    [HttpPost("licensing/verify-ledger")]
    [HasPermission(Permissions.Licenses.VerifyLedger)]
    [ProducesResponseType<LedgerRunDto>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> VerifyLedger([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] VerifyLedgerRequest? request, CancellationToken cancellationToken) =>
        ToActionResult(await _verification.StartAsync(request?.LicenseId, cancellationToken), run => Accepted($"/api/v1/admin/licensing/verify-ledger/runs/{run.Id}", run));

    /// <summary>Progress and, once finished, the result of a ledger check: counts and the first broken row of each failing license.</summary>
    [HttpGet("licensing/verify-ledger/runs/{id:guid}")]
    [HasPermission(Permissions.Licenses.VerifyLedger)]
    [ProducesResponseType<LedgerRunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyLedgerRun(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _verification.GetRunAsync(id, cancellationToken));

    /// <summary>The most recent checks (manual and nightly), newest first.</summary>
    [HttpGet("licensing/verify-ledger/runs")]
    [HasPermission(Permissions.Licenses.VerifyLedger)]
    public async Task<IActionResult> VerifyLedgerRuns(CancellationToken cancellationToken) =>
        ToActionResult(await _verification.ListRunsAsync(cancellationToken));

    /// <summary>
    /// The operator-visible alert list: ledger findings that are still open. Each is alerted once when first found (not every night);
    /// <c>lastReminderAt</c> shows the occasional reminder.
    /// </summary>
    [HttpGet("licensing/ledger-breaks")]
    [HasPermission(Permissions.Licenses.VerifyLedger)]
    [ProducesResponseType<IReadOnlyList<LedgerOpenBreakDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenLedgerBreaks(CancellationToken cancellationToken) =>
        ToActionResult(await _verification.ListOpenBreaksAsync(cancellationToken));
}
