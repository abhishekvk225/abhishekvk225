using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Dashboards;

public interface IClientDashboardService
{
    Task<Result<ClientDashboardDto>> GetAsync(DashboardQuery query, CancellationToken cancellationToken);
}

public interface IAdminDashboardService
{
    Task<Result<AdminDashboardDto>> GetAsync(DashboardQuery query, CancellationToken cancellationToken);
}

internal static class DashboardWindow
{
    public const double LatencyQuantile = 0.95;

    public static Result<int> ResolveDays(DashboardQuery query, DashboardOptions options)
    {
        var days = query.Days ?? options.DefaultDays;
        return days < 1 || days > options.MaxDays
            ? Error.Validation($"days must be between 1 and {options.MaxDays}.", new Dictionary<string, string[]> { ["days"] = [$"Must be between 1 and {options.MaxDays}."] })
            : days;
    }
}

/// <summary>
/// The caller's own numbers. Credits come straight from the ledger (the same rows as the credit history), recognitions from the
/// request history, traffic from the API request log. The client is the credential's client; nothing is accepted from the request.
/// </summary>
public sealed class ClientDashboardService : IClientDashboardService
{
    private readonly IDashboardQueries _queries;
    private readonly IClientLicenseService _licenses;
    private readonly ICurrentUser _currentUser;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _time;

    public ClientDashboardService(IDashboardQueries queries, IClientLicenseService licenses, ICurrentUser currentUser, IOptions<DashboardOptions> options, TimeProvider time)
    {
        _queries = queries;
        _licenses = licenses;
        _currentUser = currentUser;
        _options = options.Value;
        _time = time;
    }

    public async Task<Result<ClientDashboardDto>> GetAsync(DashboardQuery query, CancellationToken cancellationToken)
    {
        if (_currentUser.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Only client accounts have a client dashboard.");
        }

        var days = DashboardWindow.ResolveDays(query, _options);
        if (!days.IsSuccess)
        {
            return days.Error!;
        }

        var (from, to) = DashboardMath.Window(_time.GetUtcNow().UtcDateTime, days.Value);
        var labels = DashboardMath.Days(from, to);

        var license = await _licenses.GetSummaryAsync(cancellationToken);
        if (!license.IsSuccess)
        {
            return license.Error!;
        }

        var recognitionRows = await _queries.GetRecognitionsAsync(from, to, cancellationToken);
        var recognitionsPerDay = DashboardMath.RecognitionsPerDay(labels, recognitionRows);
        var breakdown = recognitionRows
            .GroupBy(r => (Date: DateOnly.FromDateTime(r.Day), r.Operation, r.Outcome))
            .Select(g => new RecognitionBreakdownDto(g.Key.Date, g.Key.Operation.ToString(), g.Key.Outcome.ToString(), g.Sum(r => r.Count)))
            .OrderBy(b => b.Date).ThenBy(b => b.Operation, StringComparer.Ordinal).ThenBy(b => b.Outcome, StringComparer.Ordinal)
            .ToList();

        var creditsPerDay = DashboardMath.CreditsPerDay(labels, await _queries.GetLedgerDaysAsync(clientId, from, to, cancellationToken));

        var apiRows = await _queries.GetApiDaysAsync(clientId, from, to, cancellationToken);
        var histogram = await _queries.GetLatencyHistogramAsync(clientId, from, to, _options.LatencyBucketMilliseconds, _options.LatencyCapMilliseconds, cancellationToken);
        var apiPerDay = DashboardMath.ApiPerDay(labels, apiRows, histogram, DashboardWindow.LatencyQuantile, _options.LatencyBucketMilliseconds);

        var topKeys = (await _queries.GetTopApiKeysAsync(from, to, _options.TopApiKeys, cancellationToken))
            .Select(k => new TopApiKeyDto(k.ApiKeyId, k.Name, k.KeyPrefix, k.Requests, k.Errors, k.LastRequestAt))
            .ToList();

        return new ClientDashboardDto(
            from, to, days.Value, license.Value!,
            DashboardMath.Summarise(recognitionsPerDay), recognitionsPerDay, breakdown,
            DashboardMath.Summarise(creditsPerDay), creditsPerDay,
            DashboardMath.Summarise(apiPerDay, histogram, DashboardWindow.LatencyQuantile, _options.LatencyBucketMilliseconds), apiPerDay,
            topKeys);
    }
}

/// <summary>
/// Platform-wide aggregates. Platform scope cannot read face tables, so volume comes from the ledger and the API request log only;
/// nothing returned can identify a person or a template.
/// </summary>
public sealed class AdminDashboardService : IAdminDashboardService
{
    private readonly IDashboardQueries _queries;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _time;

    public AdminDashboardService(IDashboardQueries queries, IOptions<DashboardOptions> options, TimeProvider time)
    {
        _queries = queries;
        _options = options.Value;
        _time = time;
    }

    public async Task<Result<AdminDashboardDto>> GetAsync(DashboardQuery query, CancellationToken cancellationToken)
    {
        var days = DashboardWindow.ResolveDays(query, _options);
        if (!days.IsSuccess)
        {
            return days.Error!;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var (from, to) = DashboardMath.Window(now, days.Value);
        var labels = DashboardMath.Days(from, to);

        var clientsByStatus = (await _queries.GetClientStatusCountsAsync(cancellationToken))
            .Select(c => new CountByLabelDto(c.Status.ToString(), c.Count)).OrderBy(c => c.Label, StringComparer.Ordinal).ToList();
        var licensesByStatus = DashboardMath.EffectiveStatusCounts(await _queries.GetLicenseStatusCountsAsync(now, cancellationToken), now);

        var expiring = await _queries.GetExpiringLicensesAsync(now, now.AddDays(_options.ExpiringWithinDays), _options.AttentionListSize, cancellationToken);
        var low = await _queries.GetLowBalanceLicensesAsync(now, _options.LowBalancePercent, _options.AttentionListSize, cancellationToken);

        var ledger = await _queries.GetLedgerDaysAsync(null, from, to, cancellationToken);
        var creditsPerDay = DashboardMath.CreditsPerDay(labels, ledger);
        var billed = ledger.Where(r => r.Type == LedgerEntryType.Consume && r.Operation is not null)
            .GroupBy(r => (Date: DateOnly.FromDateTime(r.Day), Operation: r.Operation!.Value))
            .Select(g => new BilledOperationDto(g.Key.Date, g.Key.Operation.ToString(), g.Sum(r => r.Entries)))
            .OrderBy(b => b.Date).ThenBy(b => b.Operation, StringComparer.Ordinal)
            .ToList();

        var topClients = (await _queries.GetTopClientsAsync(from, to, _options.TopClients, cancellationToken))
            .Select(c => new TopClientDto(c.ClientId, c.ClientCode, c.ClientName, c.CreditsConsumed, c.BilledOperations))
            .ToList();

        var apiRows = await _queries.GetApiDaysAsync(null, from, to, cancellationToken);
        var histogram = await _queries.GetLatencyHistogramAsync(null, from, to, _options.LatencyBucketMilliseconds, _options.LatencyCapMilliseconds, cancellationToken);
        var apiPerDay = DashboardMath.ApiPerDay(labels, apiRows, histogram, DashboardWindow.LatencyQuantile, _options.LatencyBucketMilliseconds);

        return new AdminDashboardDto(
            from, to, days.Value, clientsByStatus, licensesByStatus,
            Attention(expiring, _options.ExpiringWithinDays), Attention(low, _options.LowBalancePercent),
            DashboardMath.Summarise(creditsPerDay), creditsPerDay, billed, topClients,
            DashboardMath.Summarise(apiPerDay, histogram, DashboardWindow.LatencyQuantile, _options.LatencyBucketMilliseconds), apiPerDay,
            await _queries.GetWebhookHealthAsync(from, to, cancellationToken));
    }

    private static LicenseAttentionListDto Attention(LicenseAttentionPage page, int threshold) =>
        new(threshold, page.Count, page.Items.Select(i => new LicenseAttentionDto(
            i.LicenseId, i.ClientId, i.ClientName, i.LicenseName, i.TotalCredits - i.ConsumedCredits, i.TotalCredits,
            DashboardMath.PercentRemaining(i.TotalCredits, i.ConsumedCredits), i.ExpiresAt)).ToList());
}
