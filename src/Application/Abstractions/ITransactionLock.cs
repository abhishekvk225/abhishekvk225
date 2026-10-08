namespace NexaVerify.Application.Abstractions;

/// <summary>
/// A lock that lives exactly as long as the current database transaction. Taking it before a "count, then insert" makes
/// the check atomic against parallel requests of the same resource (caps such as maximum API keys, people or users).
/// </summary>
public interface ITransactionLock
{
    /// <summary>
    /// Waits for the named lock and holds it until the surrounding transaction ends. Must be called inside
    /// <see cref="IUnitOfWork.ExecuteInTransactionAsync{T}"/>; fails (rather than silently not locking) outside a transaction.
    /// </summary>
    Task AcquireAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Raised inside a unit of work to abort it because a cap would be exceeded; the caller maps it to a conflict result.</summary>
public sealed class CapExceededException : Exception
{
    public CapExceededException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
