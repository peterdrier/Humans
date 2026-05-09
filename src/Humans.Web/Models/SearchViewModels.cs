using Humans.Application.DTOs;
using Humans.Application.Interfaces.Search;

namespace Humans.Web.Models;

/// <summary>
/// View-model for the global <c>/Search</c> page. Built by
/// <c>SearchController</c> from the <see cref="GlobalSearchResults"/>
/// returned by <see cref="ISearchService"/>.
/// </summary>
public sealed class GlobalSearchViewModel
{
    public string? Query { get; init; }

    /// <summary>
    /// When set, only this type's results are shown. Drives the active
    /// filter chip in the view.
    /// </summary>
    public SearchResultType? Filter { get; init; }

    /// <summary>
    /// Human hits already projected for the canonical
    /// <c>_HumanSearchResults</c> partial.
    /// </summary>
    public IReadOnlyList<HumanSearchResultViewModel> HumanResults { get; init; } =
        Array.Empty<HumanSearchResultViewModel>();

    public IReadOnlyList<GlobalSearchResult> TeamResults { get; init; } =
        Array.Empty<GlobalSearchResult>();

    public IReadOnlyList<GlobalSearchResult> CampResults { get; init; } =
        Array.Empty<GlobalSearchResult>();

    public IReadOnlyList<GlobalSearchResult> ShiftResults { get; init; } =
        Array.Empty<GlobalSearchResult>();

    public int HumanCount => HumanResults.Count;
    public int TeamCount => TeamResults.Count;
    public int CampCount => CampResults.Count;
    public int ShiftCount => ShiftResults.Count;
    public int TotalCount => HumanCount + TeamCount + CampCount + ShiftCount;

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool QueryIsTooShort => HasQuery && (Query?.Trim().Length ?? 0) < 2;
    public bool HasResults => TotalCount > 0;
}
