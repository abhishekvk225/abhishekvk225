using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Licensing;
using NexaVerify.Infrastructure.Platform.KeyRotation;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.Background;

/// <summary>
/// Optional (<c>Encryption:LazyRewrap=true</c>, off by default): after <c>Encryption:CurrentKeyId</c> changes, moves client data keys and
/// MFA secrets onto it in small batches while the API serves traffic. One node at a time (cluster lock). It does not re-anchor ledger
/// checkpoints and does not retire anything: finish with <c>Migrator rotate-master-key</c> and <c>rotation-status</c>.
/// </summary>
public sealed partial class KeyRewrapJob : BackgroundService
{
    private readonly MasterKeyRotationService _rotation;
    private readonly IDistributedLock _lock;
    private readonly EncryptionOptions _options;
    private readonly ILogger<KeyRewrapJob> _logger;

    public KeyRewrapJob(MasterKeyRotationService rotation, IDistributedLock distributedLock, IOptions<EncryptionOptions> options, ILogger<KeyRewrapJob> logger)
    {
        _rotation = rotation;
        _lock = distributedLock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.LazyRewrap)
        {
            return;
        }

        LogEnabled(_logger, _options.LazyRewrapBatchSize, _options.LazyRewrapIntervalSeconds);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.LazyRewrapIntervalSeconds));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One batch; never throws. Returns the number of rows moved. Public for tests.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await _lock.TryAcquireAsync("key-rewrap", cancellationToken);
            if (lease is null)
            {
                return 0; // another node is doing it
            }

            var report = await _rotation.RewrapSomeAsync(_options.LazyRewrapBatchSize, cancellationToken);
            return report.ClientKeysRewrapped + report.MfaSecretsReencrypted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, ex.GetType().Name);
            return 0;
        }
    }

    [LoggerMessage(EventId = 7108, Level = LogLevel.Information, Message = "Lazy master-key re-wrap is ON: {Batch} rows every {Seconds}s after a CurrentKeyId change")]
    private static partial void LogEnabled(ILogger logger, int batch, int seconds);

    [LoggerMessage(EventId = 7109, Level = LogLevel.Error, Message = "Lazy master-key re-wrap batch failed ({ErrorType}); will retry")]
    private static partial void LogFailed(ILogger logger, string errorType);
}
