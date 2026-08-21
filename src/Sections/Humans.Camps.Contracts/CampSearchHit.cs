namespace Humans.Camps.Contracts;

/// <summary>
/// One camp that matched a global-search query. Returned by
/// <c>ICampService.SearchAsync</c>, already scored by this section — the
/// orchestrator ranks by <see cref="Score"/> and passes <see cref="CampId"/> to
/// <c>&lt;vc:camps-search-result&gt;</c>, which fetches everything it renders
/// (nobodies-collective/Humans#1062). Names-only matching: only
/// <see cref="Name"/> is matched.
/// </summary>
/// <param name="CampId">The camp's id; the key the row's view component is invoked with.</param>
/// <param name="Name">The public-year season name (the only matched field),
/// falling back to the camp's slug when no season exists for that year. Ordering
/// only — the orchestrator uses it as an alphabetical tiebreak and never renders it.</param>
/// <param name="Score">Name-match strength, per <c>StringSearchExtensions.NameMatchScore</c>.</param>
public record CampSearchHit(
    Guid CampId,
    string Name,
    int Score);
