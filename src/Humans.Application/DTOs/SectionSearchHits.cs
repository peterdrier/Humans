namespace Humans.Application.DTOs;

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
