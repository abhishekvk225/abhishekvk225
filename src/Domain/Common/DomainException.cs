namespace NexaVerify.Domain.Common;

/// <summary>Raised when a domain invariant would be violated. Carries a stable machine code.</summary>
public class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
