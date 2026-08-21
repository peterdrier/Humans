namespace Humans.Teams.Contracts;

/// <summary>
/// One team that matched a global-search query. Returned by
/// <c>ITeamService.SearchAsync</c>, already scored by this section — the
/// orchestrator (<c>SearchService</c>) ranks by <see cref="Score"/> and passes
/// <see cref="TeamId"/> to <c>&lt;vc:teams-search-result&gt;</c>, which fetches
/// everything it renders (nobodies-collective/Humans#1062). Names-only
/// matching: only <see cref="Name"/> is matched.
/// </summary>
/// <param name="TeamId">The team's id; the key the row's view component is invoked with.</param>
/// <param name="Name">Display name (the only matched field). Ordering only —
/// the orchestrator uses it as an alphabetical tiebreak and never renders it.</param>
/// <param name="Score">Name-match strength, per <c>StringSearchExtensions.NameMatchScore</c>.</param>
public record TeamSearchHit(
    Guid TeamId,
    string Name,
    int Score);
