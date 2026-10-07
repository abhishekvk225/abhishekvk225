using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Tenancy;

/// <summary>One typed setting of one client (value stored as JSON; definitions, defaults and bounds live in code).</summary>
public sealed class ClientSetting : AuditableEntity, ITenantOwned
{
    private ClientSetting()
    {
    }

    public Guid ClientId { get; set; }

    public string Key { get; private set; } = string.Empty;

    public string ValueJson { get; private set; } = "null";

    public byte[] RowVersion { get; private set; } = [];

    public static ClientSetting Create(Guid clientId, string key, string valueJson) =>
        new() { ClientId = clientId, Key = key, ValueJson = valueJson };

    public void SetValue(string valueJson) => ValueJson = valueJson;
}
