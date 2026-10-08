using System.Globalization;
using MudBlazor;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Web.Components;

namespace NexaVerify.Web.Services;

/// <summary>Real dashboard client: GET /admin/dashboard and /client/dashboard mapped into the UI view-models.</summary>
public sealed class DashboardApiClient(IApiGateway api) : IDashboardApiClient
{
    public async Task<ApiResult<AdminDashboardModel>> GetAdminDashboardAsync(DashboardRange range, CancellationToken ct = default)
    {
        var result = await api.GetAsync<AdminDashboardDto>($"admin/dashboard?days={(int)range}", ct);
        return result.IsSuccess ? ApiResult<AdminDashboardModel>.Ok(DashboardMapper.ToModel(result.Value)) : ApiResult<AdminDashboardModel>.Fail(result.Error!);
    }

    public async Task<ApiResult<ClientDashboardModel>> GetClientDashboardAsync(DashboardRange range, CancellationToken ct = default)
    {
        var result = await api.GetAsync<ClientDashboardDto>($"client/dashboard?days={(int)range}", ct);
        return result.IsSuccess ? ApiResult<ClientDashboardModel>.Ok(DashboardMapper.ToModel(result.Value)) : ApiResult<ClientDashboardModel>.Fail(result.Error!);
    }
}

/// <summary>Pure DTO to view-model mapping (formatting and grouping only; every figure comes from the API).</summary>
public static class DashboardMapper
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static AdminDashboardModel ToModel(AdminDashboardDto dto)
    {
        var activeClients = dto.ClientsByStatus.Where(c => string.Equals(c.Label, "Active", StringComparison.OrdinalIgnoreCase)).Sum(c => c.Count);
        var totalCredits = dto.CreditsPerDay.Select(d => (double)d.Consumed).ToList();
        var requests = dto.ApiPerDay.Select(d => (double)d.Requests).ToList();

        var kpis = new List<KpiModel>
        {
            new("Active clients", UiText.Number(activeClients), Icons.Material.Outlined.Business, null, true, null),
            new("Credits used", UiText.Number(dto.Credits.Net), Icons.Material.Outlined.Toll, null, true, totalCredits),
            new("API requests", UiText.Number(dto.Api.Requests), Icons.Material.Outlined.Api, null, true, requests),
            new("Failed requests", $"{(dto.Api.ErrorRate * 100m).ToString("0.0", Inv)}%", Icons.Material.Outlined.ErrorOutline, null, false, dto.ApiPerDay.Select(d => (double)d.Errors).ToList()),
            new("Licenses expiring soon", UiText.Number(dto.ExpiringLicenses.Count), Icons.Material.Outlined.EventBusy, null, false, null),
            new("Licenses running low", UiText.Number(dto.LowBalanceLicenses.Count), Icons.Material.Outlined.BatteryAlert, null, false, null),
        };

        var requestsTrend = dto.ApiPerDay.Count == 0
            ? []
            : new List<TrendSeries>
            {
                new("Successful", dto.ApiPerDay.Select(d => new TrendPoint(Day(d.Date), Math.Max(0, d.Requests - d.Errors))).ToList()),
                new("Failed", dto.ApiPerDay.Select(d => new TrendPoint(Day(d.Date), d.Errors)).ToList()),
            };
        var creditsTrend = dto.CreditsPerDay.Count == 0
            ? []
            : new List<TrendSeries> { new("Credits used", dto.CreditsPerDay.Select(d => new TrendPoint(Day(d.Date), d.Net)).ToList()) };

        return new AdminDashboardModel(
            kpis,
            requestsTrend,
            creditsTrend,
            dto.TopClients.Select(c => new ClientUsageRow(c.ClientName, c.BilledOperations, c.CreditsConsumed)).ToList(),
            dto.ExpiringLicenses.Items.Select(ToRow).ToList(),
            dto.LowBalanceLicenses.Items.Select(ToRow).ToList(),
            dto.ClientsByStatus.Select(c => new StatusCount(c.Label, c.Count)).ToList(),
            AdminAlerts(dto));
    }

    private static ExpiringLicenseRow ToRow(LicenseAttentionDto l) =>
        new(l.ClientName, l.LicenseName, new DateTimeOffset(DateTime.SpecifyKind(l.ExpiresAt, DateTimeKind.Utc)), l.RemainingCredits, l.TotalCredits);

    private static List<AlertModel> AdminAlerts(AdminDashboardDto dto)
    {
        var alerts = new List<AlertModel>();
        if (dto.Webhooks.EndpointsFailing > 0 || dto.Webhooks.EndpointsDisabled > 0)
        {
            alerts.Add(new AlertModel("warning", $"{dto.Webhooks.EndpointsFailing} webhook endpoint(s) are failing and {dto.Webhooks.EndpointsDisabled} are switched off."));
        }

        if (dto.Webhooks.AbandonedInPeriod > 0)
        {
            alerts.Add(new AlertModel("warning", $"{UiText.Number(dto.Webhooks.AbandonedInPeriod)} webhook deliveries were given up on in this period."));
        }

        if (dto.ExpiringLicenses.Count > 0)
        {
            alerts.Add(new AlertModel("warning", $"{UiText.Number(dto.ExpiringLicenses.Count)} license(s) expire soon."));
        }

        if (dto.LowBalanceLicenses.Count > 0)
        {
            alerts.Add(new AlertModel("warning", $"{UiText.Number(dto.LowBalanceLicenses.Count)} license(s) are close to running out of credits."));
        }

        if (dto.Api.ServerErrors > 0)
        {
            alerts.Add(new AlertModel("error", $"{UiText.Number(dto.Api.ServerErrors)} requests failed because of a problem on our side."));
        }

        return alerts;
    }

    public static ClientDashboardModel ToModel(ClientDashboardDto dto)
    {
        var licenses = dto.Licenses;
        var spark = dto.RecognitionsPerDay.Select(d => (double)d.Total).ToList();
        var kpis = new List<KpiModel>
        {
            new("Face checks", UiText.Number(dto.Recognitions.Total), Icons.Material.Outlined.Face, null, true, spark),
            new("Credits used", UiText.Number(dto.Credits.Net), Icons.Material.Outlined.Toll, null, false, dto.CreditsPerDay.Select(d => (double)d.Net).ToList()),
            new("Successful", UiText.Number(dto.Recognitions.Successful), Icons.Material.Outlined.CheckCircle, null, true, dto.RecognitionsPerDay.Select(d => (double)d.Successful).ToList()),
            new("Couldn't be processed", UiText.Number(dto.Recognitions.Failed), Icons.Material.Outlined.ErrorOutline, null, false, dto.RecognitionsPerDay.Select(d => (double)d.Failed).ToList()),
        };

        var dates = dto.RecognitionsPerDay.Select(d => d.Date).OrderBy(d => d).ToList();
        var byOperation = dto.RecognitionBreakdown
            .GroupBy(b => b.Operation)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var perDay = g.GroupBy(b => b.Date).ToDictionary(x => x.Key, x => x.Sum(b => b.Count));
                return new TrendSeries(g.Key, dates.Select(d => new TrendPoint(Day(d), perDay.GetValueOrDefault(d))).ToList());
            })
            .ToList();

        var recent = dto.RecognitionBreakdown
            .OrderByDescending(b => b.Date)
            .ThenBy(b => b.Operation, StringComparer.Ordinal)
            .Take(10)
            .Select((b, i) => new RecognitionRow(
                i.ToString(Inv),
                new DateTimeOffset(b.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                b.Operation,
                b.Outcome,
                UiText.Number(b.Count)))
            .ToList();

        var alerts = new List<AlertModel>();
        if (!string.Equals(licenses.Health, "Healthy", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new AlertModel(licenses.Health is "Low" or "Expiring" ? "warning" : "error", licenses.Message));
        }

        return new ClientDashboardModel(
            licenses.RemainingCredits,
            licenses.TotalCredits,
            licenses.NextExpiry is { } expiry ? new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)) : null,
            licenses.Licenses.FirstOrDefault()?.PlanName,
            licenses.Message,
            kpis,
            new OutcomeSplit(dto.Recognitions.Successful, dto.Recognitions.NoMatch, dto.Recognitions.Failed),
            byOperation,
            recent,
            new ApiUsageModel(dto.Api.Requests, (double)(dto.Api.ErrorRate * 100m), dto.Api.P95LatencyMs),
            alerts);
    }

    private static string Day(DateOnly date) => date.ToString("d MMM", Inv);
}
