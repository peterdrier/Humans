using Humans.Users.Contracts;

namespace Humans.Search.Services.Dtos;

/// <summary>
/// The five result kinds. Drives the per-type filter chips and the type-grouped
/// headings on <c>/Search</c>.
/// </summary>
internal enum SearchResultType
{
    Human = 0,
    Team = 1,
    Camp = 2,
    Shift = 3,
    Event = 4,
}

/// <summary>
/// One non-human hit: id and ordering only. Every display field is fetched by the
/// owning section's own <c>&lt;vc:…-search-result&gt;</c>, which the view invokes
/// with <see cref="Key"/>. Humans use <see cref="HumanSearchResult"/> on the same terms.
/// </summary>
/// <param name="Type">Team, Camp, Shift or Event.</param>
/// <param name="Key">The owning section's entity id.</param>
/// <param name="SortKey">Tiebreak for the controller's secondary sort; never rendered.</param>
/// <param name="Score">The owning section's score; higher is better.</param>
internal sealed record GlobalSearchResult(
    SearchResultType Type,
    Guid Key,
    string SortKey,
    int Score);

/// <summary>
/// Output of one global-search call: the trimmed query and five scored, unsorted
/// buckets. <see cref="Events"/> is empty when <c>Features:Events</c> is off.
/// </summary>
internal sealed record GlobalSearchResults(
    string Query,
    IReadOnlyList<HumanSearchResult> Humans,
    IReadOnlyList<GlobalSearchResult> Teams,
    IReadOnlyList<GlobalSearchResult> Camps,
    IReadOnlyList<GlobalSearchResult> Shifts,
    IReadOnlyList<GlobalSearchResult> Events);
