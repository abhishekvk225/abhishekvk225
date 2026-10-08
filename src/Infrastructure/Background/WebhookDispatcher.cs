using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Domain.Api;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Platform;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Delivers queued webhook events: signed JSON POST, short timeout, no redirects, connects only to addresses that pass the SSRF
/// check, exponential-backoff retries from the outbox, and automatic disabling of an endpoint that keeps failing.
/// </summary>
public sealed class WebhookDispatcher : BackgroundService
{
    private const int BatchSize = 50;
    private const int Parallelism = 8;
    private const int PoisonMaxAttempts = 3;
    private static readonly TimeSpan SuspendedRecheck = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;

    public WebhookDispatcher(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, ILogger<WebhookDispatcher> logger, TimeProvider time)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
        _time = time;
        _http = BuildClient(_options);

        // The lease must outlive the worst-case batch (every slot waiting for its full timeout), or another node could claim a
        // row that is still in flight and send it twice.
        var worstCaseRounds = (int)Math.Ceiling(BatchSize / (double)Parallelism);
        _lease = TimeSpan.FromSeconds((worstCaseRounds * (Math.Clamp(_options.TimeoutSeconds, 1, 30) + 5)) + 30);
    }

    private readonly TimeSpan _lease;

    private static HttpClient BuildClient(WebhookOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // The address that is connected to is the address that was checked: no gap for DNS rebinding.
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var allowed = options.AllowUnsafeTargets ? addresses : addresses.Where(WebhookUrlGuard.IsPublic).ToArray();
                if (allowed.Length == 0)
                {
                    throw new HttpRequestException("The target does not resolve to a public address.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(allowed[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 30)) };
    }

    private const int DeliveryRetentionDays = 30;

    private DateTime _lastPurge = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BackgroundEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchDueAsync(stoppingToken);
                await PurgeFinishedAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Webhook dispatch cycle failed");
            }
        }
    }

    /// <summary>Finished deliveries (payloads included) are kept 30 days for troubleshooting, then removed in small batches, at most hourly.</summary>
    public async Task PurgeFinishedAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - _lastPurge < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastPurge = now;
        var cutoff = now.AddDays(-DeliveryRetentionDays);
        await using var scope = _scopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("webhook delivery retention");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int removed;
        do
        {
            removed = await db.WebhookDeliveries
                .Where(d => d.Status != DeliveryStatus.Pending && d.CreatedAt < cutoff)
                .OrderBy(d => d.Id).Take(1000)
                .ExecuteDeleteAsync(cancellationToken);
        }
        while (removed == 1000);
    }

    /// <summary>Claims and delivers everything currently due; returns how many deliveries were attempted. Public for tests.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<long> ids;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("webhook claim");
            ids = await scope.ServiceProvider.GetRequiredService<WebhookStore>().ClaimDueAsync(BatchSize, _time.GetUtcNow().UtcDateTime, _lease, cancellationToken);
        }

        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (id, token) =>
            {
                try
                {
                    await DeliverAsync(id, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Webhook delivery {DeliveryId} failed unexpectedly", id);
                }
            });
        return ids.Count;
    }

    private async Task DeliverAsync(long id, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("webhook delivery");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IClientEncryption>();

        var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (delivery is null || delivery.Status != DeliveryStatus.Pending)
        {
            return;
        }

        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        if (endpoint is null || (endpoint.Status != WebhookStatus.Active && delivery.EventType != WebhookEvents.Test))
        {
            delivery.Abandon("Endpoint is disabled or was removed.", now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // The endpoint row is looked up by id only; never deliver one client's event to another client's endpoint.
        if (endpoint.ClientId != delivery.ClientId)
        {
            delivery.Abandon("The endpoint does not belong to the delivery's client.", now);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogError("Webhook delivery {DeliveryId} references endpoint {EndpointId} of another client and was abandoned", id, endpoint.Id);
            return;
        }

        // A suspended or inactive client receives nothing; the event stays queued and is re-checked later.
        if (await scope.ServiceProvider.GetRequiredService<IClientAccessGuard>().CheckAsync(delivery.ClientId, cancellationToken) is not null)
        {
            delivery.Defer(now.Add(SuspendedRecheck));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        int? statusCode = null;
        string? error = null;
        var poison = false;
        try
        {
            var secret = Encoding.UTF8.GetString(await encryption.DecryptAsync(endpoint.ClientId, endpoint.SecretEnc, "webhook-secret", cancellationToken));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Signature", WebhookSigning.Sign(secret, new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds(), delivery.PayloadJson));
            request.Headers.Add("X-Event-Id", delivery.EventId.ToString());
            request.Headers.Add("X-Event-Type", delivery.EventType);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NexaVerify-Webhooks", "1"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                error = $"The endpoint answered HTTP {statusCode}.";
            }
        }
        // Shutdown is not the receiver's fault: let it propagate so nothing is counted. Anything else (timeout, network, an
        // undecryptable secret, a malformed URL) is a failed attempt, so a poison delivery always advances toward abandonment.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            poison = ex is System.Security.Cryptography.CryptographicException or UriFormatException or FormatException;
            error = ex is TaskCanceledException ? "The endpoint did not answer in time."
                : poison ? "The delivery could not be prepared (secret or URL is unusable)."
                : "The endpoint could not be reached.";
            _logger.LogInformation("Webhook {DeliveryId} to endpoint {EndpointId} failed: {Reason}", id, endpoint.Id, ex.GetType().Name);
        }

        var finished = _time.GetUtcNow().UtcDateTime;
        if (error is null)
        {
            delivery.MarkDelivered(statusCode!.Value, finished);
            await db.SaveChangesAsync(cancellationToken);
            await db.WebhookEndpoints.Where(e => e.Id == endpoint.Id && e.FailureCount > 0).ExecuteUpdateAsync(s => s.SetProperty(e => e.FailureCount, 0), cancellationToken);
            return;
        }

        delivery.MarkFailed(statusCode, error, finished);
        if (poison && delivery.Status == DeliveryStatus.Pending && delivery.Attempts >= PoisonMaxAttempts)
        {
            // Retrying cannot fix an undecryptable secret or a malformed URL: stop after a few attempts instead of every backoff step.
            delivery.Abandon(error, finished);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (delivery.EventType != WebhookEvents.Test)
        {
            await RecordEndpointFailureAsync(db, endpoint, finished, cancellationToken);
        }
    }

    private async Task RecordEndpointFailureAsync(AppDbContext db, WebhookEndpoint endpoint, DateTime now, CancellationToken cancellationToken)
    {
        await db.WebhookEndpoints.Where(e => e.Id == endpoint.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FailureCount, e => e.FailureCount + 1), cancellationToken);
        var disabled = await db.WebhookEndpoints
            .Where(e => e.Id == endpoint.Id && e.Status == WebhookStatus.Active && e.FailureCount >= WebhookEndpoint.DisableAfterConsecutiveFailures)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, WebhookStatus.Disabled)
                .SetProperty(e => e.DisabledAt, now)
                .SetProperty(e => e.DisabledReason, "Disabled automatically after repeated delivery failures."), cancellationToken);
        if (disabled > 0)
        {
            _logger.LogWarning("Webhook endpoint {EndpointId} of client {ClientId} was disabled after repeated failures", endpoint.Id, endpoint.ClientId);
        }
    }
}
