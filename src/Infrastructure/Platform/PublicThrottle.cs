using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Public;
using NexaVerify.Domain.Api;

namespace NexaVerify.Infrastructure.Platform;

/// <summary>
/// Limits for the anonymous sign-up and contact endpoints, counted in the shared counters so they hold across API nodes. Fixed windows
/// (one UTC hour for IP limits, one UTC day for the per-email limit). The counter key is a hash of (policy, subject): no address or IP is
/// stored. The "day" counter kind is used for all windows because it is the longest-retained one.
/// </summary>
public sealed class PublicThrottle : IPublicThrottle
{
    private readonly SharedWindowCounters _counters;
    private readonly SignupOptions _options;
    private readonly TimeProvider _time;

    public PublicThrottle(SharedWindowCounters counters, IOptions<SignupOptions> options, TimeProvider time)
    {
        _counters = counters;
        _options = options.Value;
        _time = time;
    }

    public Task<bool> TryAcquireAsync(PublicThrottlePolicy policy, string subject, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var (limit, window, subjectKey) = policy switch
        {
            PublicThrottlePolicy.SignupPerIp => (_options.MaxSignupsPerIpPerHour, TimeSpan.TicksPerHour, NormalizeIp(subject)),
            PublicThrottlePolicy.ContactPerIp => (_options.MaxContactsPerIpPerHour, TimeSpan.TicksPerHour, NormalizeIp(subject)),
            PublicThrottlePolicy.SignupPerEmail => (_options.MaxSignupsPerEmailPerDay, TimeSpan.TicksPerDay, subject.Trim().ToLowerInvariant()),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown public throttle policy."),
        };

        return _counters.TryTakeAsync(UsageCounterKinds.Day, KeyFor(policy, subjectKey), now.Ticks / window, limit, cancellationToken);
    }

    /// <summary>IPv6 callers can rotate through a whole /64, so they share one budget per /64 (as the global limiter does).</summary>
    public static string NormalizeIp(string subject)
    {
        if (!IPAddress.TryParse(subject, out var address))
        {
            return subject.Trim().ToLowerInvariant();
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>A stable 128-bit key for (policy, subject).</summary>
    public static Guid KeyFor(PublicThrottlePolicy policy, string subject) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("public:" + policy + ":" + subject))[..16]);
}
