using Humans.Search.Services.Dtos;

namespace Humans.Search.Models;

internal sealed class GlobalSearchViewModel
{
    public string? Query { get; init; }

    public string? Filter { get; init; }

    public bool SearchFailed { get; init; }

    /// <summary>Every enabled category in weight order, each bucket's hits in display order.</summary>
    public IReadOnlyList<SearchBucket> Buckets { get; init; } = [];

    public int TotalCount => Buckets.Sum(b => b.Hits.Count);

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool QueryIsTooShort => HasQuery && (Query?.Trim().Length ?? 0) < 2;
    public bool HasResults => TotalCount > 0;
}
