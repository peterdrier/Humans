using Humans.Search.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Search.Models;

/// <summary>
/// View-model for <c>/Search</c>: the query, the active filter chip, and five buckets in
/// display order. The view hands each row to its owning section's view component.
/// </summary>
internal sealed class GlobalSearchViewModel
{
    public string? Query { get; init; }

    public SearchResultType? Filter { get; init; }

    public IReadOnlyList<HumanSearchResult> HumanResults { get; init; } =
        [];

    public IReadOnlyList<GlobalSearchResult> TeamResults { get; init; } =
        [];

    public IReadOnlyList<GlobalSearchResult> CampResults { get; init; } =
        [];

    public IReadOnlyList<GlobalSearchResult> ShiftResults { get; init; } =
        [];

    public IReadOnlyList<GlobalSearchResult> EventResults { get; init; } =
        [];

    public int HumanCount => HumanResults.Count;
    public int TeamCount => TeamResults.Count;
    public int CampCount => CampResults.Count;
    public int ShiftCount => ShiftResults.Count;
    public int EventCount => EventResults.Count;
    public int TotalCount => HumanCount + TeamCount + CampCount + ShiftCount + EventCount;

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool QueryIsTooShort => HasQuery && (Query?.Trim().Length ?? 0) < 2;
    public bool HasResults => TotalCount > 0;
}
