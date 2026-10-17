using Humans.Base.Interfaces;
using Humans.Base.Attributes;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// The writes the Teams section makes on a team's linked Google resources, alongside the
/// read-only <see cref="ITeamResourceService"/>: link, unlink, permission-level changes and
/// restricting inherited Drive access. Everything else the resource service does is internal to
/// <c>Humans.GoogleIntegration</c>.
/// </summary>
public interface ITeamResourceWrite : IApplicationService
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
}

public sealed record TeamResourceMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static TeamResourceMutationResult Success() => new(true, null);

    public static TeamResourceMutationResult Failed(string message) => new(false, message);
}
