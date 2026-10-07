namespace NexaVerify.Domain.Common;

/// <summary>
/// Marks an entity whose rows may only ever be inserted (ledgers, audit trails, login history).
/// Persistence installs database triggers that reject UPDATE and DELETE for these tables.
/// </summary>
public interface IAppendOnly
{
}
