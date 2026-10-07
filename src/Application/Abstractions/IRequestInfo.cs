namespace NexaVerify.Application.Abstractions;

/// <summary>Network context of the current request (for audit/login history). Never used for authorisation.</summary>
public interface IRequestInfo
{
    string? IpAddress { get; }

    string? UserAgent { get; }

    string? CorrelationId { get; }
}
