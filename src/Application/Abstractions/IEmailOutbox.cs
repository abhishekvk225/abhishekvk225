namespace NexaVerify.Application.Abstractions;

/// <summary>
/// Hands an email to a background dispatcher and returns immediately, so request latency never depends on (or leaks
/// through) the mail transport — important for flows whose response time must not reveal whether an account exists.
/// </summary>
public interface IEmailOutbox
{
    void Enqueue(EmailMessage message);
}
