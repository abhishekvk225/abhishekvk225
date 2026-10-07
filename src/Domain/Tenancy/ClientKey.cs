using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Tenancy;

public enum ClientKeyStatus
{
    Active = 0,
    Retired,
    Destroyed,
}

/// <summary>A client's data-encryption key, stored only wrapped (encrypted) by the master key. Destroying them crypto-shreds the client's data.</summary>
public sealed class ClientKey : Entity, ITenantOwned
{
    private ClientKey()
    {
    }

    public Guid ClientId { get; set; }

    public int KeyVersion { get; private set; }

    [AuditIgnore]
    public byte[] WrappedDataKey { get; private set; } = [];

    public string MasterKeyId { get; private set; } = string.Empty;

    public ClientKeyStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? RetiredAt { get; private set; }

    public static ClientKey Create(Guid clientId, int version, byte[] wrapped, string masterKeyId, DateTime now) =>
        new() { ClientId = clientId, KeyVersion = version, WrappedDataKey = wrapped, MasterKeyId = masterKeyId, Status = ClientKeyStatus.Active, CreatedAt = now };

    public void Retire(DateTime now)
    {
        Status = ClientKeyStatus.Retired;
        RetiredAt = now;
    }

    /// <summary>Irreversibly removes the key material. Anything encrypted under it becomes unreadable.</summary>
    public void Destroy(DateTime now)
    {
        WrappedDataKey = [];
        Status = ClientKeyStatus.Destroyed;
        RetiredAt ??= now;
    }
}
