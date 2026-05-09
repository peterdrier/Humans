namespace Humans.Application.DTOs;

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

/// <summary>
/// One camp that matched a global-search query. Returned by
/// <c>ICampService.SearchAsync</c> with the public-year season's display
/// name already resolved so the orchestrator never has to traverse
/// <c>Camp.Seasons</c> to render the row. Names-only matching: only
/// <see cref="Name"/> is matched.
/// </summary>
/// <param name="Slug">URL slug; used for the detail URL and as subtitle.</param>
/// <param name="Name">The public-year season name (the only matched field),
/// falling back to the camp's slug when no season exists for that year.</param>
public record CampSearchHit(
    string Slug,
    string Name);

/// <summary>
/// One rota that matched a global-search query. Returned by
/// <c>IShiftManagementService.SearchAsync</c> with the owning team's name
/// already stitched so the orchestrator never has to call
/// <c>ITeamService</c> just to render the subtitle. Names-only matching:
/// only <see cref="Name"/> is matched.
/// </summary>
/// <param name="Name">Rota display name (the only matched field).</param>
/// <param name="TeamId">Owning team id; drives the rota detail URL.</param>
/// <param name="TeamName">Owning team display name; surfaced as subtitle.</param>
public record RotaSearchHit(
    string Name,
    Guid TeamId,
    string TeamName);
