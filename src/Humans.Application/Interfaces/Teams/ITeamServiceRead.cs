using Humans.Application.Architecture;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Teams;

/// <summary>
/// Cross-section read surface for the Teams section. External sections inject
/// this interface; only <see cref="TeamInfo"/> / <see cref="TeamSearchHit"/>
/// projections, no EF entities. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
[SurfaceBudget(4)]
public interface ITeamServiceRead
{
    /// <summary>
    /// Gets team read models keyed by ID, including active members.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the team read model by ID, including active members.
    /// </summary>
    Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the team read model by slug (matches the canonical
    /// <c>Slug</c> or the optional <c>CustomSlug</c>).
    /// </summary>
    Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active, non-hidden teams whose <c>Name</c> contains
    /// <paramref name="query"/> (case-insensitive). Capped at
    /// <paramref name="max"/>; returned in unspecified order — the global
    /// search orchestrator scores and ranks.
    /// </summary>
    Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);
}
