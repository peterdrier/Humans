using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Aggregate summary of a team's Google resources, used by admin listings that need
/// per-team flags/counts without loading the full resource rows.
/// </summary>
public record TeamResourceSummary(bool HasMailGroup, int DriveResourceCount)
{
    public static TeamResourceSummary Empty { get; } = new(false, 0);
}

/// <summary>
/// A Google resource projection joined with its owning team, used by the dashboard
/// "My Google Resources" widget. Owned by ITeamResourceService so callers do not
/// need to reach across the team ↔ resource boundary.
/// </summary>
public record UserTeamGoogleResource(
    string TeamName,
    string TeamSlug,
    string ResourceName,
    GoogleResourceType ResourceType,
    string? Url);

public record GoogleResourceSnapshot(
    Guid Id,
    Guid TeamId,
    string GoogleId,
    string Name,
    GoogleResourceType ResourceType,
    string? Url,
    Instant ProvisionedAt = default,
    Instant? LastSyncedAt = null,
    bool IsActive = true,
    string? ErrorMessage = null,
    DrivePermissionLevel DrivePermissionLevel = DrivePermissionLevel.None,
    bool RestrictInheritedAccess = false);

/// <summary>
/// Cross-section Google resource reads. Every write on a team's linked resources is internal to
/// <c>Humans.GoogleIntegration</c> (its team-resources page at <c>/Google/Teams/{slug}/Resources</c>).
/// </summary>
public interface ITeamResourceService : IApplicationService
{
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
