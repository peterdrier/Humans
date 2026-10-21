using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Full team-resource surface, and the sole owner of the <c>google_resources</c> table: callers
/// must never access <c>DbSet&lt;GoogleResource&gt;</c> directly. Other sections depend on
/// <see cref="ITeamResourceService"/> for reads; every write — link, unlink, permission level,
/// inherited-access restriction, sync bookkeeping — is internal to the section.
/// </summary>
internal interface ITeamResourceManagement : ITeamResourceService, IApplicationService
{
    /// <summary>
    /// Links an existing Google Drive folder or file (Sheet, Doc, etc.) to a team by URL.
    /// Detects the resource type from the URL. The item must be on a Shared Drive
    /// pre-shared with the service account as Editor.
    /// </summary>
    Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Links an existing Google Group to a team by email address.
    /// The service account must be added as a Group Manager.
    /// </summary>
    Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Unlinks a resource from a team (soft-delete: sets IsActive = false).
    /// </summary>
    Task UnlinkResourceAsync(Guid resourceId, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Updates the Drive permission level for a resource.
    /// </summary>
    Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Sets the RestrictInheritedAccess flag on a Drive folder resource and immediately
    /// enforces the corresponding inheritedPermissionsDisabled setting on Google Drive,
    /// returning a result that captures any Google Drive mutation failure.
    /// </summary>
    [ExternalWrite]
    Task<TeamResourceMutationResult> SetRestrictInheritedAccessWithResultAsync(Guid resourceId, bool restrict, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the total active-resource count for every team that currently has any,
    /// regardless of resource type. Used by admin aggregates (e.g. email rename impact).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActiveResourceCountsByTeamAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks a Google resource reconciliation as successful and clears any
    /// previously recorded sync error.
    /// </summary>
    Task MarkResourceSyncedAsync(Guid resourceId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Records the last Google reconciliation error for a linked team resource.
    /// </summary>
    Task RecordResourceErrorAsync(Guid resourceId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Gets the active Google resources visible to a user, joined with their team metadata.
    /// Dashboard "My Google Resources" widget.
    /// </summary>
    Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamResourcesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of Google resource rows (including inactive).
    /// Used by the observable metrics gauge.
    /// </summary>
    Task<int> GetResourceCountAsync(CancellationToken ct = default);

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
    /// Returns the display name for each resource id. Missing ids are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetResourceNamesByIdsAsync(
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken ct = default);
}

internal sealed record TeamResourceMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static TeamResourceMutationResult Success() => new(true, null);

    public static TeamResourceMutationResult Failed(string message) => new(false, message);
}
