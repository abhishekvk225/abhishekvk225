using Microsoft.AspNetCore.Components;

namespace NexaVerify.Web.Components;

/// <summary>Column definition for <see cref="DataTable{T}"/>. Supply <see cref="Value"/> for plain text or <see cref="Template"/> for rich cells.</summary>
public sealed class DataColumn<T>
{
    public required string Header { get; init; }

    /// <summary>Sort key sent to the API as <c>sort=key:asc|desc</c>. Null = not sortable.</summary>
    public string? SortKey { get; init; }

    public Func<T, string?>? Value { get; init; }

    public RenderFragment<T>? Template { get; init; }

    /// <summary>Right-align numbers.</summary>
    public bool Numeric { get; init; }

    public static DataColumn<T> Text(string header, Func<T, string?> value, string? sortKey = null, bool numeric = false) =>
        new() { Header = header, Value = value, SortKey = sortKey, Numeric = numeric };

    public static DataColumn<T> Custom(string header, RenderFragment<T> template, string? sortKey = null) =>
        new() { Header = header, Template = template, SortKey = sortKey };
}
