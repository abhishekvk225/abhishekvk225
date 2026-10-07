using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Identity;

/// <summary>A single capability key (e.g. <c>licenses.manage</c>). The code-side catalogue is the source of truth; rows are synced at startup.</summary>
public sealed class Permission : Entity
{
    private Permission()
    {
    }

    public string Key { get; private set; } = string.Empty;

    public string Group { get; private set; } = string.Empty;

    public PermissionScope Scope { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public static Permission Create(string key, string group, PermissionScope scope, string description) =>
        new() { Key = key, Group = group, Scope = scope, Description = description };

    public void Update(string group, PermissionScope scope, string description)
    {
        Group = group;
        Scope = scope;
        Description = description;
    }
}
