namespace Humans.Application.DTOs;

/// <summary>
/// Top-level result type for the global /Search page. Drives both the
/// per-type group filter chips ("All | Humans | Teams | Camps | Shifts")
/// and the badge rendered next to each result row.
/// </summary>
public enum SearchResultType
{
    Human = 0,
    Team = 1,
    Camp = 2,
    Shift = 3,
}

/// <summary>
/// One row in the global search result page. Each entry knows its type,
/// canonical detail URL, a short subtitle, and a score that the orchestrator
/// uses to rank the merged list. Cross-modal "relational" hits (e.g. teams a
/// matched person belongs to) reuse this same shape with a non-null
/// <see cref="RelationContext"/>.
/// </summary>
/// <param name="Type">Whether this is a Human, Team, Camp, or Shift hit.</param>
/// <param name="Title">Primary display label (BurnerName, team name, camp
/// name, rota name).</param>
/// <param name="Subtitle">Optional secondary line — typically the matched
/// snippet, slug, or for Shifts the owning team name.</param>
/// <param name="Url">Canonical detail-page URL for the entity.</param>
/// <param name="Score">Higher = better match. Composed by the orchestrator
/// from match-strength + multi-field boost. Display ordering is descending
/// Score, then Title.</param>
/// <param name="UserId">Owning user id when <see cref="Type"/> is
/// <see cref="SearchResultType.Human"/>; otherwise null. Drives the
/// avatar / popover render path.</param>
/// <param name="MatchField">Short label for which field matched
/// ("Name", "Bio", "Slug", "Description", etc.). Null when the row is a
/// pure relational pull-in.</param>
/// <param name="RelationContext">Optional explanation when the row was
/// surfaced as a relational hit ("Lead at <em>Foo Camp</em>",
/// "Coordinator of <em>Bar Team</em>", "Shift in <em>Baz Team</em>").
/// Null on direct hits.</param>
public record GlobalSearchResult(
    SearchResultType Type,
    string Title,
    string? Subtitle,
    string Url,
    int Score,
    Guid? UserId = null,
    string? MatchField = null,
    string? RelationContext = null);
