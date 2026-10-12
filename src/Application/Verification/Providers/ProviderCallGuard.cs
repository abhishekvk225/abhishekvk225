using Microsoft.Extensions.Logging;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Providers;

/// <summary>Stable reason codes of the external stage.</summary>
public static class ProviderReasonCodes
{
    public const string NotConfigured = "PROVIDER_NOT_CONFIGURED";
    public const string Timeout = "PROVIDER_TIMEOUT";
    public const string Error = "PROVIDER_ERROR";
    public const string ReferenceMissing = "PROVIDER_REFERENCE_MISSING";
    public const string InvalidResult = "PROVIDER_INVALID_RESULT";
    public const string InsufficientData = "INSUFFICIENT_DATA";
}

/// <summary>
/// The single place that turns a provider call into an <see cref="ExternalStageOutcome"/>: it enforces the per-call timeout, maps every
/// exception to Timeout/ProviderError (nothing expected propagates), and enforces Rule V-1: a <c>Verified</c> answer without a provider
/// id and reference is downgraded, so no code path can produce "verified" without evidence. It logs codes only, never request data.
/// </summary>
public static class ProviderCallGuard
{
    public static async Task<ExternalStageOutcome> RunAsync(
        string providerId, bool isConfigured, int timeoutSeconds, Func<CancellationToken, Task<ProviderResult>> call, ILogger logger, CancellationToken cancellationToken)
    {
        if (!isConfigured)
        {
            return new ExternalStageOutcome(ExternalState.ProviderNotConfigured, providerId, null, [], ProviderReasonCodes.NotConfigured);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            var result = await call(timeout.Token).ConfigureAwait(false);
            return Map(providerId, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Provider call timed out ({Provider})", providerId);
            return new ExternalStageOutcome(ExternalState.Timeout, providerId, null, [], ProviderReasonCodes.Timeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Provider call failed ({Provider}, {ExceptionType})", providerId, ex.GetType().Name);
            return new ExternalStageOutcome(ExternalState.ProviderError, providerId, null, [], ProviderReasonCodes.Error);
        }
    }

    public static ExternalStageOutcome Map(string providerId, ProviderResult result)
    {
        switch (result.State)
        {
            case ExternalState.Verified:
                return string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(result.ProviderReference)
                    ? new ExternalStageOutcome(ExternalState.ProviderError, providerId, null, [], ProviderReasonCodes.ReferenceMissing)
                    : new ExternalStageOutcome(ExternalState.Verified, providerId, result.ProviderReference, result.FieldMatches, result.ReasonCode);
            case ExternalState.NotVerified:
            case ExternalState.Inconclusive:
            case ExternalState.ProviderError:
            case ExternalState.Timeout:
            case ExternalState.ProviderNotConfigured:
                return new ExternalStageOutcome(result.State, providerId, result.ProviderReference, result.FieldMatches, result.ReasonCode);
            default: // NotRequested / Pending are not answers a provider may give
                return new ExternalStageOutcome(ExternalState.ProviderError, providerId, null, [], ProviderReasonCodes.InvalidResult);
        }
    }
}
