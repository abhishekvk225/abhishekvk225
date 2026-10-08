namespace NexaVerify.Application.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="action"/> inside a database transaction (commit on success, rollback on throw).</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);

    /// <summary>Forgets every tracked entity (e.g. after a concurrency conflict) so the next read sees current database state.</summary>
    void ClearTracked();
}
