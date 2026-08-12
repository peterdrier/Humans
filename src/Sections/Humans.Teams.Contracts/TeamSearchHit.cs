namespace Humans.Teams.Contracts;

/// <summary>
/// One team that matched a global-search query. Returned by
/// <c>ITeamService.SearchAsync</c>; the orchestrator (<c>SearchService</c>)
/// scores by name-match strength and renders. Names-only matching: only
/// <see cref="Name"/> is matched at the DB layer; <see cref="Slug"/> is
/// carried through for URL construction and as the row subtitle.
/// </summary>
/// <param name="Name">Display name (the only matched field).</param>
/// <param name="Slug">URL slug; used for the detail URL and as subtitle.</param>
public record TeamSearchHit(
    string Name,
    string Slug);
