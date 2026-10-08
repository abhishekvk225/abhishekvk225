using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Common;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Layer 2 (per credential, per minute) and layer 3 (per client, per UTC day) of the throttling design. The counters are shared
/// between API nodes through <see cref="SharedWindowCounters"/> (SQL Server, atomic, bucketed and purged) with an in-memory fast path;
/// limits come from the client's settings (cached 30 s) — never from literals. A key may lower its own per-minute limit, never raise it.
/// </summary>
public sealed class ApiUsageLimiter : IApiUsageLimiter
{
    private static readonly TimeSpan LimitsTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly SharedWindowCounters _counters;
    private readonly ConcurrentDictionary<Guid, (DateTime LoadedAt, int PerMinute, long PerDay)> _limits = new();

    public ApiUsageLimiter(IServiceScopeFactory scopes, TimeProvider time, SharedWindowCounters counters)
    {
        _scopes = scopes;
        _time = time;
        _counters = counters;
    }

    public async Task<(Error? Error, TimeSpan RetryAfter)> TryAcquireAsync(Guid clientId, Guid credentialId, int? credentialLimitPerMinute, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var (perMinute, perDay) = await LimitsAsync(clientId, now, cancellationToken);

        // A credential may be tighter than the account limit, never looser (a client admin cannot raise their own ceiling).
        var limit = credentialLimitPerMinute is { } own ? Math.Min(own, perMinute) : perMinute;
        var minute = now.Ticks / TimeSpan.TicksPerMinute;
        var day = DateOnly.FromDateTime(now).DayNumber;

        if (!await _counters.TryTakeAsync(UsageCounterKinds.Minute, credentialId, minute, limit, cancellationToken))
        {
            var retry = TimeSpan.FromSeconds(60 - (now.Second + (now.Millisecond / 1000.0)));
            return (Error.TooManyRequests(ErrorCodes.RateLimited, "Rate limit exceeded for this credential. Retry after the indicated time."), retry);
        }

        if (!await _counters.TryTakeAsync(UsageCounterKinds.Day, clientId, day, perDay, cancellationToken))
        {
            _counters.Return(UsageCounterKinds.Minute, credentialId, minute); // a rejected call must not also eat the minute budget
            var retry = DateOnly.FromDateTime(now).AddDays(1).ToDateTime(TimeOnly.MinValue) - now;
            return (Error.TooManyRequests(ErrorCodes.DailyQuotaExceeded, "The daily request quota for this account has been used up."), retry);
        }

        return (null, TimeSpan.Zero);
    }

    private async Task<(int PerMinute, long PerDay)> LimitsAsync(Guid clientId, DateTime now, CancellationToken cancellationToken)
    {
        if (_limits.TryGetValue(clientId, out var cached) && now - cached.LoadedAt < LimitsTtl)
        {
            return (cached.PerMinute, cached.PerDay);
        }

        int perMinute = Convert.ToInt32(SettingCatalog.Find(SettingKeys.Api.RateLimitPerMinute)!.Default, CultureInfo.InvariantCulture);
        long perDay = Convert.ToInt64(SettingCatalog.Find(SettingKeys.Api.DailyQuota)!.Default, CultureInfo.InvariantCulture);

        await using var scope = _scopes.CreateAsyncScope();
        // The caller is an authenticated tenant principal: read its own client's settings in its own tenant scope (platform scope is off limits to it).
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ClientSettings.AsNoTracking()
            .Where(s => s.ClientId == clientId && (s.Key == SettingKeys.Api.RateLimitPerMinute || s.Key == SettingKeys.Api.DailyQuota))
            .Select(s => new { s.Key, s.ValueJson })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            if (!TryNumber(row.ValueJson, out var value) || value < 1)
            {
                continue;
            }

            if (row.Key == SettingKeys.Api.RateLimitPerMinute)
            {
                perMinute = (int)Math.Min(value, int.MaxValue);
            }
            else
            {
                perDay = value;
            }
        }

        _limits[clientId] = (now, perMinute, perDay);
        return (perMinute, perDay);
    }

    private static bool TryNumber(string json, out long value)
    {
        value = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt64(out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
