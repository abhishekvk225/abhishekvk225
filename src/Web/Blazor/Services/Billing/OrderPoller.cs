namespace NexaVerify.Web.Services;

/// <summary>
/// Waits for the API to say what happened to an order after the person came back from the payment page. The address the browser came
/// back with (<c>?result=success</c>) is never proof of anything: only the order the API returns counts. Polls with a growing delay
/// for up to <see cref="MaxWait"/>, stops as soon as the order is settled, and ends cleanly when cancelled. All waiting goes through the
/// supplied <see cref="TimeProvider"/> so tests can drive it with a fake clock.
/// </summary>
public sealed class OrderPoller(TimeProvider clock)
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>1 s, 2 s, 3 s, then every 5 s.</summary>
    public static TimeSpan DelayFor(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(1),
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Fetches until the order is no longer pending, a definite error arrives (not found, no permission, session gone), or the time is up.
    /// <paramref name="onResult"/> sees every answer. Returns the last answer; <c>TimedOut</c> is true when the order was still pending at the end.
    /// </summary>
    public async Task<PollOutcome> RunAsync(
        Func<CancellationToken, Task<ApiResult<OrderDto>>> fetch,
        Func<ApiResult<OrderDto>, Task> onResult,
        CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var attempt = 0;
        ApiResult<OrderDto> last;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            last = await fetch(ct);
            await onResult(last);

            if (last.IsSuccess ? last.Value.Status != OrderStatuses.Pending : !IsTransient(last.Error!))
            {
                return new PollOutcome(last, false);
            }

            var delay = DelayFor(attempt++);
            if (clock.GetUtcNow() - started + delay > MaxWait)
            {
                return new PollOutcome(last, true);
            }

            await Task.Delay(delay, clock, ct);
        }
    }

    /// <summary>A hiccup worth another try: service unreachable, timeout, 5xx or 429. Everything else is final.</summary>
    public static bool IsTransient(ApiError error) => PhotoGuard.IsRetryable(error);
}

public sealed record PollOutcome(ApiResult<OrderDto> Last, bool TimedOut);
