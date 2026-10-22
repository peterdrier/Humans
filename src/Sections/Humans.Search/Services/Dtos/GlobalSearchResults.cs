using Humans.Users.Contracts;

namespace Humans.Search.Services.Dtos;

internal enum SearchResultType
{
    Human = 0,
    Team = 1,
    Camp = 2,
    Shift = 3,
    Event = 4,
}

/// <summary>
/// Id and ordering only: the owning section's view component renders the row from <paramref name="Key"/>.
/// <paramref name="SortKey"/> is a tiebreak, never rendered; a higher <paramref name="Score"/> is better.
/// </summary>
internal sealed record GlobalSearchResult(
    SearchResultType Type,
    Guid Key,
    string SortKey,
    int Score);

/// <summary>Buckets are unsorted. <see cref="Events"/> is empty when <c>Features:Events</c> is off.</summary>
internal sealed record GlobalSearchResults(
    string Query,
    IReadOnlyList<HumanSearchResult> Humans,
    IReadOnlyList<GlobalSearchResult> Teams,
    IReadOnlyList<GlobalSearchResult> Camps,
    IReadOnlyList<GlobalSearchResult> Shifts,
    IReadOnlyList<GlobalSearchResult> Events);
