using Humans.Users.Contracts;

namespace Humans.Search.Services.Dtos;

/// <summary>
/// Top-level result type for the global /Search page. Drives the per-type
/// group filter chips ("All | Humans | Teams | Camps | Shifts | Events") and the
/// type-grouped section headers in the results view.
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
/// One non-human row in a global search result group (Team, Camp, Shift, or
/// Event). Ids and ordering only — every display field is fetched by the owning
/// section's own <c>&lt;vc:…-search-result&gt;</c>, which the view invokes with
/// <see cref="Key"/> (nobodies-collective/Humans#1062). Humans use
/// <see cref="HumanSearchResult"/> on the same terms.
/// </summary>
/// <param name="Type">Whether this is a Team, Camp, Shift, or Event hit.</param>
/// <param name="Key">The owning section's entity id for the row, passed straight
/// to the view component's tag attribute. Every bucket keys by Guid.</param>
/// <param name="SortKey">Alphabetical tiebreak for the controller's secondary
/// sort. Never rendered; naming another section's display field here would put
/// its vocabulary back in this section.</param>
/// <param name="Score">Higher = better match, scored by the owning section
/// (exact &gt; prefix &gt; contains). The controller orders each type bucket by
/// descending Score then ascending SortKey.</param>
internal sealed record GlobalSearchResult(
    SearchResultType Type,
    Guid Key,
    string SortKey,
    int Score);

/// <summary>
/// Aggregated output of a single global-search call. Each type bucket is
/// already ranked within itself; the view renders them as separate sections.
/// </summary>
/// <param name="Query">Echo of the trimmed input.</param>
/// <param name="Humans">Human hits in the order returned by the
/// profile-search matcher. The controller sorts them by relevance and the view
/// passes each row's id and match context to Users'
/// <c>&lt;vc:user-search-result&gt;</c> — this section builds no display
/// model.</param>
/// <param name="Teams">Team hits, scored but in unspecified order — the
/// controller sorts by score desc then sort key asc before rendering.</param>
/// <param name="Camps">Camp hits, scored but in unspecified order — the
/// controller sorts by score desc then sort key asc before rendering.</param>
/// <param name="Shifts">Rota (shift) hits, scored but in unspecified order
/// — the controller sorts by score desc then sort key asc before rendering.</param>
/// <param name="Events">Approved event hits, scored but in unspecified order —
/// the controller sorts by score desc then sort key asc before rendering. Empty
/// when the <c>Features:Events</c> flag is off.</param>
internal sealed record GlobalSearchResults(
    string Query,
    IReadOnlyList<HumanSearchResult> Humans,
    IReadOnlyList<GlobalSearchResult> Teams,
    IReadOnlyList<GlobalSearchResult> Camps,
    IReadOnlyList<GlobalSearchResult> Shifts,
    IReadOnlyList<GlobalSearchResult> Events);
