using Microsoft.AspNetCore.Components;

namespace NexaVerify.Web.Components;

/// <summary>One label/value pair of a <see cref="DetailList"/>. Supply <see cref="Content"/> for chips or links instead of plain text.</summary>
public sealed record DetailItem(string Label, string? Value = null, RenderFragment? Content = null);
