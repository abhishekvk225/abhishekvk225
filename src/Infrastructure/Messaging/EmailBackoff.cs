namespace NexaVerify.Infrastructure.Messaging;

/// <summary>Retry timing: exponential (doubling) from a base delay, capped, randomised so a recovering mail server is not hit by every retry at once.</summary>
public static class EmailBackoff
{
    /// <param name="attemptsMade">Attempts already used (1 = the first attempt just failed).</param>
    /// <param name="random">A value in [0,1), injected so the calculation is deterministic in tests.</param>
    public static TimeSpan Delay(int attemptsMade, EmailDispatcherOptions options, double random)
    {
        var exponent = Math.Clamp(attemptsMade - 1, 0, 30);
        var seconds = Math.Min((double)options.MaxDelaySeconds, options.BaseDelaySeconds * Math.Pow(2, exponent));
        var jitter = options.JitterPercent / 100.0;
        seconds *= 1 + (((Math.Clamp(random, 0, 1) * 2) - 1) * jitter);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, options.MaxDelaySeconds));
    }
}
