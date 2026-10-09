using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Common;
using NexaVerify.Contracts.Common;

namespace NexaVerify.Application.Billing;

public interface IBillingWebhookService
{
    /// <summary>
    /// Handles a provider notification. The signature over the raw body is verified BEFORE the body is parsed and before any database
    /// work; a bad signature is a 400 and leaves no trace but a log line. Everything after that is idempotent.
    /// </summary>
    Task<Result> HandleAsync(string providerName, IReadOnlyDictionary<string, string> headers, byte[] rawBody, CancellationToken cancellationToken);
}

public sealed partial class BillingWebhookService : IBillingWebhookService
{
    private readonly IPaymentProviderResolver _providers;
    private readonly IPaymentEventProcessor _processor;
    private readonly BillingOptions _options;
    private readonly ILogger<BillingWebhookService> _logger;

    public BillingWebhookService(
        IPaymentProviderResolver providers, IPaymentEventProcessor processor, IOptions<BillingOptions> options, ILogger<BillingWebhookService> logger)
    {
        _providers = providers;
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result> HandleAsync(string providerName, IReadOnlyDictionary<string, string> headers, byte[] rawBody, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _providers.Find(providerName) is not { } provider)
        {
            return Error.NotFound();
        }

        if (rawBody.Length == 0 || rawBody.Length > _options.WebhookMaxBodyBytes)
        {
            return Error.Validation("The request body is empty or too large.");
        }

        var parsed = provider.VerifyAndParseWebhook(headers, rawBody);
        switch (parsed.Status)
        {
            case WebhookParseStatus.InvalidSignature:
                LogRejected(provider.Name);
                return Error.Validation("The webhook signature is not valid.");

            case WebhookParseStatus.Malformed:
                LogMalformed(provider.Name);
                return Error.Validation("The webhook body could not be read.");

            case WebhookParseStatus.Ignored:
                return Result.Success();
        }

        var processed = await _processor.ProcessAsync(parsed.Event!, cancellationToken);
        return processed.IsSuccess ? Result.Success() : processed.Error!;
    }

    [LoggerMessage(EventId = 9004, Level = LogLevel.Warning, Message = "Payment webhook for {Provider} rejected: signature not valid")]
    private partial void LogRejected(string provider);

    [LoggerMessage(EventId = 9017, Level = LogLevel.Warning, Message = "Payment webhook for {Provider} rejected: body not readable")]
    private partial void LogMalformed(string provider);
}
