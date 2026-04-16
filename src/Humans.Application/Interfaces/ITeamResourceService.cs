using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

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

/// <summary>
/// Service for linking and managing pre-shared Google resources for teams.
/// Unlike IGoogleSyncService (which provisions new resources), this service
/// validates and links existing resources that have been pre-shared with the service account.
///
/// This service is the sole owner of the <c>google_resources</c> table: callers must
/// never access <c>DbSet&lt;GoogleResource&gt;</c> directly and must go through the
/// read methods on this interface instead.
/// </summary>
public interface ITeamResourceService
{
    /// <summary>
    /// Gets all active Google resources linked to a single team, ordered by provision time.
    /// </summary>
    Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Gets all active Google resources for a set of teams, grouped by team id.
    /// Missing team ids map to an empty list in the returned dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<GoogleResource>>> GetResourcesByTeamIdsAsync(
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
    /// Returns the total active-resource count for every team that currently has any,
    /// regardless of resource type. Used by admin aggregates (e.g. email rename impact).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the active Google resources visible to a user, joined with their team metadata.
    /// Dashboard "My Google Resources" widget.
    /// </summary>
    Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamResourcesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets every active Drive folder resource across all teams.
    /// Used by Drive activity anomaly detection.
    /// </summary>
    Task<IReadOnlyList<GoogleResource>> GetActiveDriveFoldersAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of Google resource rows (including inactive).
    /// Used by the observable metrics gauge.
    /// </summary>
    Task<int> GetResourceCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Links an existing Google Drive folder to a team by URL.
    /// The folder must be pre-shared with the service account as Editor.
    /// </summary>
    Task<LinkResourceResult> LinkDriveFolderAsync(Guid teamId, string folderUrl, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default);

    /// <summary>
    /// Links an existing Google Drive file (Sheet, Doc, etc.) to a team by URL.
    /// The file must be on a Shared Drive pre-shared with the service account.
    /// </summary>
    Task<LinkResourceResult> LinkDriveFileAsync(Guid teamId, string fileUrl, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default);

    /// <summary>
    /// Links a Google Drive resource (folder or file) to a team by URL.
    /// Automatically detects the resource type from the URL and routes accordingly.
    /// </summary>
    Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default);

    /// <summary>
    /// Links an existing Google Group to a team by email address.
    /// The service account must be added as a Group Manager.
    /// </summary>
    Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, CancellationToken ct = default);

    /// <summary>
    /// Unlinks a resource from a team (soft-delete: sets IsActive = false).
    /// </summary>
    Task UnlinkResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Deactivates Google resources owned by a team (soft-delete: IsActive = false) and
    /// writes an audit log entry for each. Called by the Google reconciliation sync after
    /// a soft-deleted team's resources have had their access revoked, so the rows stop
    /// being processed by further reconciliation ticks.
    /// </summary>
    /// <param name="teamId">Team whose resources should be deactivated.</param>
    /// <param name="resourceType">
    /// If set, only resources of this type are deactivated. Required when the caller has
    /// only reconciled one resource type — deactivating the other types before they have
    /// been reconciled would cause the next per-type sync to skip them and leave Google
    /// access in place.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task DeactivateResourcesForTeamAsync(
        Guid teamId,
        GoogleResourceType? resourceType = null,
        CancellationToken ct = default);

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
    /// Gets a single Google resource by ID, including its team.
    /// </summary>
    Task<GoogleResource?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Updates the Drive permission level for a resource.
    /// </summary>
    Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, CancellationToken ct = default);

    /// <summary>
    /// Sets the RestrictInheritedAccess flag on a Drive folder resource and immediately
    /// enforces the corresponding inheritedPermissionsDisabled setting on Google Drive.
    /// </summary>
    Task SetRestrictInheritedAccessAsync(Guid resourceId, bool restrict, CancellationToken ct = default);
}
