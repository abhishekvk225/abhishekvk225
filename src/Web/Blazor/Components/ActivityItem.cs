namespace NexaVerify.Web.Components;

/// <summary>One row of the activity feed. Text is always rendered as text.</summary>
public sealed record ActivityItem(string Id, string Title, string? Detail, DateTimeOffset At, string Icon, MudBlazor.Color Color = MudBlazor.Color.Default);
