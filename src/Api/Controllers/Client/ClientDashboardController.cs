using Microsoft.AspNetCore.Mvc;
using NexaVerify.Api.Authorization;
using NexaVerify.Api.Filters;
using NexaVerify.Application.Api;
using NexaVerify.Api.Http;
using NexaVerify.Application.Dashboards;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Identity;

namespace NexaVerify.Api.Controllers.Client;

/// <summary>A client's own usage dashboard, usage export and notification feed. The client always comes from the credential.</summary>
[Route("api/v1/client")]
public sealed class ClientDashboardController : ApiControllerBase
{
    private readonly IClientDashboardService _dashboard;
    private readonly IUsageReportService _reports;
    private readonly INotificationService _notifications;

    public ClientDashboardController(IClientDashboardService dashboard, IUsageReportService reports, INotificationService notifications)
    {
        _dashboard = dashboard;
        _reports = reports;
        _notifications = notifications;
    }

    /// <summary>Credit balances, recognitions per day, success/no-match/error rates, credits consumed per day, API traffic and top keys.</summary>
    [HttpGet("dashboard")]
    [HasPermission(Permissions.Dashboard.Client)]
    [Throttle(ThrottlePolicies.Dashboards)]
    [ProducesResponseType<ClientDashboardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard([FromQuery] DashboardQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _dashboard.GetAsync(query, cancellationToken));

    /// <summary>Daily usage as CSV (at most 92 days). Cells that could be run as formulas by a spreadsheet are neutralised; the export is audit-logged.</summary>
    [HttpGet("reports/usage.csv")]
    [HasPermission(Permissions.Usage.Read)]
    [Throttle(ThrottlePolicies.Exports)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UsageCsv([FromQuery] UsageReportQuery query, CancellationToken cancellationToken)
    {
        var result = await _reports.ExportClientAsync(query, cancellationToken);
        return result.IsSuccess ? new CsvStreamResult(result.Value) : ProblemFor(result.Error!);
    }

    [HttpGet("notifications")]
    [HasPermission(Permissions.Notifications.Read)]
    [ProducesResponseType<NotificationFeedDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Notifications([FromQuery] NotificationListQuery query, CancellationToken cancellationToken) =>
        ToActionResult(await _notifications.ListAsync(query, cancellationToken));

    [HttpPost("notifications/{id:guid}/read")]
    [HasPermission(Permissions.Notifications.Read)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _notifications.MarkReadAsync(id, cancellationToken));
}
