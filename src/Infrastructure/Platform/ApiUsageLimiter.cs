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
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Layer 2 (per credential, per minute) and layer 3 (per client, per UTC day) of the throttling design. Counters live in memory
/// per node (v1); limits come from the client's settings (cached 30 s) — never from literals. A key may lower or raise its own
/// per-minute limit.
/// </summary>
public sealed class ApiUsageLimiter : IApiUsageLimiter
{
    private static readonly TimeSpan LimitsTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, (DateTime LoadedAt, int PerMinute, long PerDay)> _limits = new();
    private readonly ConcurrentDictionary<Guid, Window> _minute = new();
    private readonly ConcurrentDictionary<Guid, Day> _day = new();
    private long _lastSweepTicks;

    public ApiUsageLimiter(IServiceScopeFactory scopes, TimeProvider time)
    {
        _scopes = scopes;
        _time = time;
    }

    private sealed class Window
    {
        public long Start;
        public int Count;
        public readonly object Gate = new();
    }

    private sealed class Day
    {
        public DateOnly Date;
        public long Count;
        public readonly object Gate = new();
    }

    public async Task<(Error? Error, TimeSpan RetryAfter)> TryAcquireAsync(Guid clientId, Guid credentialId, int? credentialLimitPerMinute, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var (perMinute, perDay) = await LimitsAsync(clientId, now, cancellationToken);
        Sweep(now);

        // Daily quota first: it is the cheaper check to fail and it must not be consumed by calls that the minute limit rejects.
        var today = DateOnly.FromDateTime(now);
        var day = _day.GetOrAdd(clientId, _ => new Day { Date = today });
        var minuteWindow = _minute.GetOrAdd(credentialId, _ => new Window { Start = MinuteOf(now) });
        var limit = credentialLimitPerMinute ?? perMinute;
        var windowStart = MinuteOf(now);

        lock (minuteWindow.Gate)
        {
            if (minuteWindow.Start != windowStart)
            {
                minuteWindow.Start = windowStart;
                minuteWindow.Count = 0;
            }

            if (minuteWindow.Count >= limit)
            {
                var retry = TimeSpan.FromSeconds(60 - (now.Second + (now.Millisecond / 1000.0)));
                return (Error.TooManyRequests(ErrorCodes.RateLimited, "Rate limit exceeded for this credential. Retry after the indicated time."), retry);
            }

            minuteWindow.Count++;
        }

        lock (day.Gate)
        {
            if (day.Date != today)
            {
                day.Date = today;
                day.Count = 0;
            }

            if (day.Count >= perDay)
            {
                lock (minuteWindow.Gate)
                {
                    minuteWindow.Count--; // a rejected call must not also eat the minute budget
                }

                var retry = today.AddDays(1).ToDateTime(TimeOnly.MinValue) - now;
                return (Error.TooManyRequests(ErrorCodes.DailyQuotaExceeded, "The daily request quota for this account has been used up."), retry);
            }

            day.Count++;
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

    private static long MinuteOf(DateTime now) => now.Ticks / TimeSpan.TicksPerMinute;

    /// <summary>Drops counters of credentials that went quiet so the maps cannot grow without bound.</summary>
    private void Sweep(DateTime now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < TimeSpan.TicksPerMinute * 5 || Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
        {
            return;
        }

        var current = MinuteOf(now);
        foreach (var (id, window) in _minute)
        {
            if (current - window.Start > 5)
            {
                _minute.TryRemove(id, out _);
            }
        }

        var today = DateOnly.FromDateTime(now);
        foreach (var (id, day) in _day)
        {
            if (day.Date != today)
            {
                _day.TryRemove(id, out _);
            }
        }
    }
}
