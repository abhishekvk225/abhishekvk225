namespace NexaVerify.Contracts.Common;

/// <summary>Standard paging/sorting query. <see cref="MaxPageSize"/> caps abusive requests.</summary>
public sealed record PageRequest
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;
    public string? Sort { get; init; }
    public string? Search { get; init; }

    /// <summary>Returns a copy with page and size clamped to valid bounds.</summary>
    public PageRequest Normalize() => this with
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
    };

    public int Skip => (Math.Max(1, Page) - 1) * Math.Clamp(PageSize, 1, MaxPageSize);
}
