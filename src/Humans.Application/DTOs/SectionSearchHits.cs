namespace Humans.Application.DTOs;

/// <summary>
/// One team that matched a global-search query. Returned by
/// <c>ITeamService.SearchAsync</c>; the orchestrator (<c>SearchService</c>)
/// scores and renders.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Slug">URL slug (also surfaced as the subtitle when the
/// description is empty).</param>
/// <param name="Description">Free-text description; nullable.</param>
public record TeamSearchHit(
    string Name,
    string Slug,
    string? Description);

/// <summary>
/// One camp that matched a global-search query. Returned by
/// <c>ICampService.SearchAsync</c> with the public-year season's display
/// name + blurb already resolved so the orchestrator never has to traverse
/// <c>Camp.Seasons</c> to render the row.
/// </summary>
/// <param name="Slug">URL slug.</param>
/// <param name="Name">The public-year season name, falling back to the
/// camp's slug when no season exists for that year.</param>
/// <param name="Blurb">Short blurb from the public-year season; nullable.</param>
public record CampSearchHit(
    string Slug,
    string Name,
    string? Blurb);

/// <summary>
/// One rota that matched a global-search query. Returned by
/// <c>IShiftManagementService.SearchAsync</c> with the owning team's name
/// already stitched so the orchestrator never has to call
/// <c>ITeamService</c> just to render the subtitle.
/// </summary>
/// <param name="Name">Rota display name.</param>
/// <param name="Description">Free-text description; nullable.</param>
/// <param name="TeamId">Owning team id; drives the rota detail URL.</param>
/// <param name="TeamName">Owning team display name.</param>
public record RotaSearchHit(
    string Name,
    string? Description,
    Guid TeamId,
    string TeamName);
