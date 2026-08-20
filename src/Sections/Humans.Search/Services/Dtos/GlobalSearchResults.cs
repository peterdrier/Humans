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
/// One non-human row in a global search result group (Team, Camp, Shift,
/// or Event). Humans use <see cref="HumanSearchResult"/> directly — the view
/// passes each hit's id to Users' own <c>&lt;vc:user-search-result&gt;</c>.
/// The four types here are still projected by this section
/// (nobodies-collective/Humans#1062 leaves them for their owners to publish).
/// </summary>
/// <param name="Type">Whether this is a Team, Camp, Shift, or Event hit.</param>
/// <param name="Title">Primary display label (team name, camp season name,
/// rota name, event title) — the only field matched against the query.</param>
/// <param name="Subtitle">Secondary line: slug for teams/camps, owning-team
/// name for shifts, category name for events.</param>
/// <param name="Url">Canonical detail-page URL for the entity.</param>
/// <param name="Score">Higher = better match. Derived from name-match
/// strength (exact > prefix > contains). The controller orders each
/// type bucket by descending Score then ascending Title.</param>
internal sealed record GlobalSearchResult(
    SearchResultType Type,
    string Title,
    string Subtitle,
    string Url,
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
/// controller sorts by score desc then title asc before rendering.</param>
/// <param name="Camps">Camp hits, scored but in unspecified order — the
/// controller sorts by score desc then title asc before rendering.</param>
/// <param name="Shifts">Rota (shift) hits, scored but in unspecified order
/// — the controller sorts by score desc then title asc before rendering.</param>
/// <param name="Events">Approved event hits, scored but in unspecified order —
/// the controller sorts by score desc then title asc before rendering. Empty
/// when the <c>Features:Events</c> flag is off.</param>
internal sealed record GlobalSearchResults(
    string Query,
    IReadOnlyList<HumanSearchResult> Humans,
    IReadOnlyList<GlobalSearchResult> Teams,
    IReadOnlyList<GlobalSearchResult> Camps,
    IReadOnlyList<GlobalSearchResult> Shifts,
    IReadOnlyList<GlobalSearchResult> Events);
