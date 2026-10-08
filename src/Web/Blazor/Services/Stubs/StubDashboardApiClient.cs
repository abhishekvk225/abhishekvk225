using NexaVerify.Web.Components;
using MudBlazor;

namespace NexaVerify.Web.Services;

/// <summary>UI-1 stub: deterministic mock data so the look can be reviewed without a backend.</summary>
public sealed class StubDashboardApiClient(TimeProvider clock) : IDashboardApiClient
{
    public async Task<ApiResult<AdminDashboardModel>> GetAdminDashboardAsync(DashboardRange range, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);
        var now = clock.GetUtcNow();
        var days = (int)range;

        var requests = Series("Successful", days, now, seed: 3, baseline: 4200, amplitude: 1400);
        var failed = Series("Failed", days, now, seed: 7, baseline: 260, amplitude: 120);
        var credits = Series("Credits used", days, now, seed: 5, baseline: 5100, amplitude: 1500);

        var kpis = new List<KpiModel>
        {
            new("Active clients", "128", Icons.Material.Outlined.Business, 4.1, true, Spark(requests, 12)),
            new("Credits available", UiText.Number(1_284_500), Icons.Material.Outlined.Toll, -2.3, true, null),
            new("Credits used", UiText.Number((long)credits.Points.Sum(p => p.Value)), Icons.Material.Outlined.TrendingUp, 12.8, true, Spark(credits, 12)),
            new("Recognition requests", UiText.Number((long)(requests.Points.Sum(p => p.Value) + failed.Points.Sum(p => p.Value))), Icons.Material.Outlined.Face, 9.4, true, Spark(requests, 12)),
            new("Failed requests", UiText.Number((long)failed.Points.Sum(p => p.Value)), Icons.Material.Outlined.ErrorOutline, 3.2, false, Spark(failed, 12)),
            new("Licenses expiring soon", "7", Icons.Material.Outlined.EventBusy, 0, false, null),
        };

        var model = new AdminDashboardModel(
            kpis,
            [requests, failed],
            [credits],
            [
                new("Acme Corp", 48_210, 51_900),
                new("Globex Bank", 39_870, 44_120),
                new("Initech", 22_904, 24_015),
                new("Umbrella Health", 18_330, 19_440),
                new("Soylent Retail", 9_112, 9_800),
            ],
            [
                new("Initech", "LIC-2025-0142", now.AddDays(4), 1_200, 50_000),
                new("Soylent Retail", "LIC-2025-0098", now.AddDays(12), 5_300, 20_000),
                new("Hooli", "LIC-2025-0177", now.AddDays(26), 18_500, 25_000),
            ],
            [
                new("Hooli", "LIC-2025-0177", now.AddDays(26), 1_800, 25_000),
            ],
            [new("Active", 128), new("Suspended", 6), new("Inactive", 3)],
            [
                new("warning", "3 licenses are close to running out of credits."),
                new("error", "Face provider response time is above normal (p95 1.9 s)."),
                new("info", "Nightly ledger check finished with no mismatches."),
            ]);

        return ApiResult<AdminDashboardModel>.Ok(model);
    }

    public async Task<ApiResult<ClientDashboardModel>> GetClientDashboardAsync(DashboardRange range, CancellationToken ct = default)
    {
        await Task.Delay(500, ct);
        var now = clock.GetUtcNow();
        var days = (int)range;

        var verify = Series("Verify", days, now, seed: 11, baseline: 320, amplitude: 110);
        var identify = Series("Identify", days, now, seed: 13, baseline: 140, amplitude: 70);
        var total = (long)(verify.Points.Sum(p => p.Value) + identify.Points.Sum(p => p.Value));
        var failedCount = total / 14;
        var noMatch = total / 9;

        return ApiResult<ClientDashboardModel>.Ok(new ClientDashboardModel(
            CreditsRemaining: 842,
            CreditsTotal: 1_000,
            LicenseExpiresAt: now.AddDays(23).AddHours(2),
            Kpis:
            [
                new("Face checks", UiText.Number(total), Icons.Material.Outlined.Face, 8.6, true, Spark(verify, 12)),
                new("Credits used", UiText.Number(158), Icons.Material.Outlined.Toll, 5.0, false, null),
                new("Successful", UiText.Number(total - noMatch - failedCount), Icons.Material.Outlined.CheckCircle, 7.9, true, null),
                new("Couldn't be processed", UiText.Number(failedCount), Icons.Material.Outlined.ErrorOutline, -1.5, false, null),
            ],
            Outcomes: new OutcomeSplit(total - noMatch - failedCount, noMatch, failedCount),
            UsageTrend: [verify, identify],
            RecentResults:
            [
                new("r1", now, "Verify", "Matched", "212"),
                new("r2", now, "Identify", "NoMatch", "9"),
                new("r3", now.AddDays(-1), "Enroll", "Enrolled", "31"),
                new("r4", now.AddDays(-1), "Verify", "NoFaceDetected", "4"),
            ],
            ApiUsage: new ApiUsageModel(12_480, 1.4, 640),
            Alerts:
            [
                new("info", "Your licence renews automatically unless you tell us otherwise."),
            ],
            PlanName: "Business",
            LicenseMessage: "842 credits available."));
    }

    private static TrendSeries Series(string name, int days, DateTimeOffset now, int seed, double baseline, double amplitude)
    {
        var points = new List<TrendPoint>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var day = now.AddDays(-i);
            var wave = Math.Sin((days - i + seed) * 0.55) * amplitude;
            var noise = ((days - i) * 37 % 11 - 5) * amplitude / 40.0;
            points.Add(new TrendPoint(day.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), Math.Round(Math.Max(0, baseline + wave + noise))));
        }

        return new TrendSeries(name, points);
    }

    private static IReadOnlyList<double> Spark(TrendSeries series, int count) =>
        series.Points.TakeLast(count).Select(p => p.Value).ToList();
}
