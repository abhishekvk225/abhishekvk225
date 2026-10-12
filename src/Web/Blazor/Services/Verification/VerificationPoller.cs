using NexaVerify.Contracts.Verification;

namespace NexaVerify.Web.Services;

/// <summary>
/// Waits for a check to settle, the same way <see cref="OrderPoller"/> waits for an order: passive calls, a growing delay, a time limit,
/// quiet retries on hiccups, a clean stop when cancelled. All waiting goes through the supplied <see cref="TimeProvider"/>.
/// </summary>
public sealed class VerificationPoller(TimeProvider clock)
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>2 s, 2 s, 3 s, then every 5 s. The first wait can be longer if the API asked for it.</summary>
    public static TimeSpan DelayFor(int attempt, int? hintSeconds = null) => attempt switch
    {
        0 => TimeSpan.FromSeconds(Math.Clamp(hintSeconds ?? 2, 1, 30)),
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(5),
    };

    /// <param name="fetch">One status call (passive).</param>
    /// <param name="onResult">Sees every answer.</param>
    /// <param name="firstHintSeconds">When set, wait before the first call (the 202 answer's hint); when null the first call is immediate.</param>
    public async Task<VerificationPollOutcome> RunAsync(
        Func<CancellationToken, Task<ApiResult<VerificationStatusDto>>> fetch,
        Func<ApiResult<VerificationStatusDto>, Task> onResult,
        CancellationToken ct,
        int? firstHintSeconds = null)
    {
        var started = clock.GetUtcNow();
        var attempt = 0;
        if (firstHintSeconds is not null)
        {
            await Task.Delay(DelayFor(0, firstHintSeconds), clock, ct);
            attempt = 1;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var last = await fetch(ct);
            await onResult(last);

            if (last.IsSuccess ? VerificationStatuses.IsTerminal(last.Value.Status) : !PhotoGuard.IsRetryable(last.Error!))
            {
                return new VerificationPollOutcome(last, false);
            }

            var delay = DelayFor(attempt++);
            if (clock.GetUtcNow() - started + delay > MaxWait)
            {
                return new VerificationPollOutcome(last, true);
            }

            await Task.Delay(delay, clock, ct);
        }
    }
}

public sealed record VerificationPollOutcome(ApiResult<VerificationStatusDto> Last, bool TimedOut);

/// <summary>
/// One logical submit. The idempotency key stays the same while the same document and details are being (re)sent, so a retry after a
/// timeout cannot be charged twice; it is replaced when either changes or the attempt ended definitively.
/// </summary>
public sealed class VerificationAttempt
{
    private readonly PhotoAttempt _inner = new();

    public string KeyFor(object request, DocumentUploadFile? document) =>
        _inner.KeyFor(new { request, document = document is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document.Data)) });

    public void Reset() => _inner.Reset();
}
