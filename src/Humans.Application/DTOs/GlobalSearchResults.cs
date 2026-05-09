namespace Humans.Application.DTOs;

/// <summary>
/// Top-level result type for the global /Search page. Drives the per-type
/// group filter chips ("All | Humans | Teams | Camps | Shifts") and the
/// type-grouped section headers in the results view.
/// </summary>
public enum SearchResultType
{
    Human = 0,
    Team = 1,
    Camp = 2,
    Shift = 3,
}

/// <summary>
/// One non-human row in a global search result group (Team, Camp, or
/// Shift). Humans use <see cref="HumanSearchResult"/> directly so the
/// canonical <c>_HumanSearchResults</c> partial can render them.
/// </summary>
/// <param name="Type">Whether this is a Team, Camp, or Shift hit.</param>
/// <param name="Title">Primary display label (team name, camp season name,
/// rota name) — the only field matched against the query.</param>
/// <param name="Subtitle">Secondary line: slug for teams/camps, owning-team
/// name for shifts.</param>
/// <param name="Url">Canonical detail-page URL for the entity.</param>
/// <param name="Score">Higher = better match. Derived from name-match
/// strength (exact > prefix > contains). The controller orders each
/// type bucket by descending Score then ascending Title.</param>
public record GlobalSearchResult(
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
/// <param name="Humans">Human hits, ordered by the profile-search matcher.
/// The view projects these to <c>HumanSearchResultViewModel</c> for the
/// canonical <c>_HumanSearchResults</c> partial.</param>
/// <param name="Teams">Team hits, score-desc then name-asc.</param>
/// <param name="Camps">Camp hits, score-desc then name-asc.</param>
/// <param name="Shifts">Rota (shift) hits, score-desc then name-asc.</param>
public record GlobalSearchResults(
    string Query,
    IReadOnlyList<HumanSearchResult> Humans,
    IReadOnlyList<GlobalSearchResult> Teams,
    IReadOnlyList<GlobalSearchResult> Camps,
    IReadOnlyList<GlobalSearchResult> Shifts);
