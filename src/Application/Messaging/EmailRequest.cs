using System.Globalization;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Application.Messaging;

/// <summary>A value to place in an email. Multi-line values keep their line breaks (a contact message); everything else is flattened to one line.</summary>
public sealed record EmailValue(string Value, bool Multiline);

/// <summary>A link inside an email, given as a path (and query) RELATIVE to the configured public base URL; the renderer builds the absolute address.</summary>
public sealed record EmailLink(string Path, IReadOnlyList<KeyValuePair<string, string>> Query);

/// <summary>
/// What a caller wants to tell someone: a template and its variables. Callers never write wording, HTML or absolute URLs; the renderer
/// encodes every variable and only emits links under the public base URL.
/// </summary>
public sealed class EmailRequest
{
    private readonly Dictionary<string, EmailValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EmailLink> _links = new(StringComparer.Ordinal);

    public EmailRequest(EmailTemplate template, string to, string? toName = null)
    {
        Template = template;
        To = to;
        ToName = toName;
    }

    public EmailTemplate Template { get; }

    public string To { get; }

    public string? ToName { get; }

    /// <summary>Optional id tying the mail to the request or job that caused it; defaults to the current trace id.</summary>
    public string? CorrelationId { get; init; }

    public IReadOnlyDictionary<string, EmailValue> Values => _values;

    public IReadOnlyDictionary<string, EmailLink> Links => _links;

    public EmailRequest Text(string key, string? value)
    {
        _values[key] = new EmailValue(value ?? string.Empty, Multiline: false);
        return this;
    }

    public EmailRequest Block(string key, string? value)
    {
        _values[key] = new EmailValue(value ?? string.Empty, Multiline: true);
        return this;
    }

    public EmailRequest Number(string key, long value) => Text(key, value.ToString("N0", CultureInfo.InvariantCulture));

    public EmailRequest Link(string key, string path, params (string Key, string Value)[] query)
    {
        _links[key] = new EmailLink(path, [.. query.Select(q => new KeyValuePair<string, string>(q.Key, q.Value))]);
        return this;
    }
}

/// <summary>
/// Hands an email to the delivery pipeline without waiting for the mail server. The message is STAGED in the current unit of work:
/// it is stored by the next <c>SaveChanges</c> of the caller, in the same transaction as the business change that caused it - so a
/// rolled-back change never sends mail and a committed one never loses it. Callers that have nothing else to save call
/// <c>IUnitOfWork.SaveChangesAsync</c> afterwards. Delivery, retries and throttling are the dispatcher's job.
/// </summary>
public interface IEmailSender
{
    /// <returns>The outbox id, or null when the message was dropped (the recipient address is unusable; logged).</returns>
    Task<Guid?> SendAsync(EmailRequest request, CancellationToken cancellationToken);
}
