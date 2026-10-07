namespace NexaVerify.Contracts.Common;

/// <summary>Standard paging/sorting query. Bounds are enforced so abusive values cannot overflow or exhaust the database.</summary>
public sealed record PageRequest
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;
    public const int MaxPage = 100_000;

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;
    public string? Sort { get; init; }
    public string? Search { get; init; }

    /// <summary>Returns a copy with page and size clamped to valid bounds.</summary>
    public PageRequest Normalize() => this with
    {
        Page = Math.Clamp(Page, 1, MaxPage),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
    };

    public int Skip
    {
        get
        {
            var n = Normalize();
            return (n.Page - 1) * n.PageSize; // max 99,999 * 100 — cannot overflow
        }
    }
}
