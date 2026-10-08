using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Api;

namespace NexaVerify.Infrastructure.Platform;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// DEVELOPMENT/TEST ONLY: allows http:// and private/loopback targets. Production refuses to start with it on, because it turns the
    /// webhook sender into a way to reach internal services (SSRF).
    /// </summary>
    public bool AllowUnsafeTargets { get; set; }

    [System.ComponentModel.DataAnnotations.Range(1, 30)]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Deliveries one dispatcher cycle (every 2 s) claims at most, across all endpoints.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>Deliveries sent at the same time by one node.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 64)]
    public int Parallelism { get; set; } = 8;

    /// <summary>
    /// Fairness/throttle: one endpoint gets at most this many of the deliveries in a cycle, so a client with a huge backlog (or a slow
    /// receiver) cannot starve everybody else. Per endpoint that is at most this many sends every 2 seconds per node.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(1, 1000)]
    public int MaxPerEndpointPerCycle { get; set; } = 5;

    /// <summary>
    /// How long a claimed delivery is reserved for this node before another node may take it (a crashed dispatcher's work reappears after it).
    /// 0 = derive it from the batch size, parallelism and timeout (the worst case of one cycle). A smaller explicit value than that is
    /// refused at startup: a delivery still in flight could be sent twice.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(0, 3600)]
    public int LeaseSeconds { get; set; }

    /// <summary>An endpoint is disabled automatically after this many consecutive failed EVENTS (an event counts once, when its first attempt fails; any success resets).</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 1000)]
    public int DisableAfterFailedEvents { get; set; } = Domain.Api.WebhookEndpoint.DisableAfterConsecutiveFailures;

    /// <summary>The lease the worst-case cycle needs: every slot waits for the full timeout, plus a margin.</summary>
    public TimeSpan DerivedLease => TimeSpan.FromSeconds((Math.Ceiling(BatchSize / (double)Parallelism) * (TimeoutSeconds + 5)) + 30);

    /// <summary>Turn the background dispatch loop off (tests drive the dispatcher by hand; a worker-only deployment can also split roles).</summary>
    public bool BackgroundEnabled { get; set; } = true;
}

/// <summary>
/// SSRF protection. A URL is acceptable only if it is https, has no credentials, and EVERY address its host resolves to is a public
/// unicast address. The same check runs again on the addresses actually connected to at send time (see <see cref="IsPublic"/>), so
/// DNS tricks between registration and delivery (rebinding) do not help an attacker.
/// </summary>
public sealed class WebhookUrlGuard : IWebhookUrlGuard
{
    private readonly WebhookOptions _options;

    public WebhookUrlGuard(IOptions<WebhookOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string?> ValidateAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            return "Enter a full URL such as https://example.com/hooks/nexaverify.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(_options.AllowUnsafeTargets && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "The URL must use https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "The URL must not contain a username or password.";
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return "The URL must not contain a fragment.";
        }

        if (_options.AllowUnsafeTargets)
        {
            return null;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal) && !IPAddress.TryParse(host, out _))
        {
            return "The URL must point to a public internet host.";
        }

        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return "The host name could not be resolved.";
        }

        return addresses.Length == 0 || addresses.Any(a => !IsPublic(a)) ? "The URL must point to a public internet host." : null;
    }

    /// <summary>True only for globally routable unicast addresses (not loopback, private, link-local, CGNAT, multicast, documentation, metadata…).</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)    // CGNAT
                || (bytes[0] == 169 && bytes[1] == 254)                // link-local incl. cloud metadata 169.254.169.254
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) // IETF protocol / TEST-NET-1
                || (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)  // 6to4 relay anycast (deprecated)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)           // benchmarking
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) // TEST-NET-2
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)  // TEST-NET-3
                || bytes[0] >= 224);                                   // multicast + reserved + broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Allow-list: only global unicast 2000::/3 can be public. That already excludes ::/8 (unspecified, IPv4-compatible
            // ::a.b.c.d, loopback), fc00::/7, fe80::/10, ff00::/8 and 64:ff9b::/96 (NAT64). The special-purpose blocks that live
            // INSIDE 2000::/3 and can embed or tunnel arbitrary IPv4 addresses are removed explicitly.
            if ((bytes[0] & 0xE0) != 0x20)
            {
                return false;
            }

            return !(address.IsIPv6Teredo                                       // 2001::/32 Teredo
                || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x02)    // 2001::/23 IETF protocol assignments (incl. Teredo, ORCHID)
                || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) // documentation 2001:db8::/32
                || (bytes[0] == 0x20 && bytes[1] == 0x02)                        // 6to4 2002::/16 embeds an IPv4 address
                || (bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0x00)  // documentation 3fff::/20
                || (bytes[8] is 0x00 or 0x02 && bytes[9] == 0x00 && bytes[10] == 0x5E && bytes[11] == 0xFE)); // ISATAP interface id embeds IPv4
        }

        return false;
    }
}
