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
    /// When set, only results of this type are shown. Drives the active
    /// filter chip in the view.
    /// </summary>
    public SearchResultType? Filter { get; init; }

    public IReadOnlyList<GlobalSearchResult> Results { get; init; } =
        Array.Empty<GlobalSearchResult>();

    /// <summary>
    /// Total counts per type from the unfiltered result set. Used to
    /// render the "Humans (N)" / "Teams (N)" / etc. counts on the chips
    /// regardless of which filter is currently active.
    /// </summary>
    public int HumanCount { get; init; }
    public int TeamCount { get; init; }
    public int CampCount { get; init; }
    public int ShiftCount { get; init; }

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool QueryIsTooShort => HasQuery && (Query?.Trim().Length ?? 0) < 2;
    public bool HasResults => Results.Count > 0;
}
