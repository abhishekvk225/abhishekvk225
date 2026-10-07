namespace NexaVerify.Web.Services;

/// <summary>One sidebar entry. <see cref="RequiredPermission"/> null means visible to every signed-in user of the portal.</summary>
public sealed record NavItem(
    string Title,
    string Href,
    string Icon,
    string? RequiredPermission = null,
    bool Exact = false,
    bool Implemented = true);

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);
