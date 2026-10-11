using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Messaging;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Messaging;

/// <summary>
/// <see cref="IEmailSender"/>: renders the mail and STAGES it in the caller's unit of work (the scoped <see cref="AppDbContext"/>), so it is
/// stored in the same transaction as the business change. It never touches the mail server; the dispatcher does that later.
/// </summary>
public sealed partial class OutboxEmailSender : IEmailSender
{
    private readonly AppDbContext _db;
    private readonly IEmailRenderer _renderer;
    private readonly EmailOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxEmailSender> _logger;

    public OutboxEmailSender(AppDbContext db, IEmailRenderer renderer, IOptions<EmailOptions> options, TimeProvider time, ILogger<OutboxEmailSender> logger)
    {
        _db = db;
        _renderer = renderer;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public Task<Guid?> SendAsync(EmailRequest request, CancellationToken cancellationToken)
    {
        var to = request.To?.Trim();
        if (EmailOptionsValidator.PlainAddress(to) is not { } address)
        {
            LogDropped(request.Template.ToString());
            return Task.FromResult<Guid?>(null);
        }

        var rendered = _renderer.Render(request);
        var fromDomain = _options.From.Address[(_options.From.Address.LastIndexOf('@') + 1)..];
        var correlation = request.CorrelationId ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        if (correlation.Length > OutboundEmail.CorrelationMaxLength)
        {
            correlation = correlation[..OutboundEmail.CorrelationMaxLength];
        }

        OutboundEmail email;
        try
        {
            email = OutboundEmail.Queue(
                address, EmailText.DisplayName(request.ToName), rendered.Subject, rendered.TextBody, rendered.HtmlBody, request.Template,
                EmailText.SingleLine(correlation), $"{Guid.NewGuid():N}@{fromDomain}", rendered.HasOneTimeToken, _time.GetUtcNow().UtcDateTime);
        }
        catch (DomainException)
        {
            LogDropped(request.Template.ToString());
            return Task.FromResult<Guid?>(null);
        }

        _db.Add(email);
        return Task.FromResult<Guid?>(email.Id);
    }

    [LoggerMessage(EventId = 8310, Level = LogLevel.Warning, Message = "An email of template {Template} was dropped: the recipient address is not usable")]
    private partial void LogDropped(string template);
}
