namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Cross-section Google resource reads. Mutations remain on <see cref="ITeamResourceService"/>.
/// </summary>
public interface ITeamResourceServiceRead
{
    /// <summary>
    /// Returns the total active-resource count for every team that currently has any,
    /// regardless of resource type. Used for resource discovery and admin aggregates (e.g. email rename impact).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all active Google resources linked to a single team.
    /// </summary>
    Task<IReadOnlyList<GoogleResourceSnapshot>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Gets all active Google resources for a set of teams, grouped by team id.
    /// Missing team ids map to an empty list in the returned dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>> GetResourcesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>
    /// Gets aggregate summaries (mail group presence, drive resource count) for a set of teams.
    /// Missing team ids map to <see cref="TeamResourceSummary.Empty"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TeamResourceSummary>> GetTeamResourceSummariesAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>
    /// Gets every active Drive folder resource across all teams.
    /// Used by Drive activity anomaly detection.
    /// </summary>
    Task<IReadOnlyList<GoogleResourceSnapshot>> GetActiveDriveFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether a user can manage resources for a team.
    /// Board members can always manage. Leads can manage if the admin setting allows it.
    /// </summary>
    Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the service account email address for display in sharing instructions.
    /// </summary>
    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a single Google resource by ID for display callers.
    /// </summary>
    Task<GoogleResourceSnapshot?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default);
}
