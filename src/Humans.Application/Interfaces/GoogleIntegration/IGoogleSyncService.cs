using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Service for provisioning and syncing Google resources.
/// </summary>
public interface IGoogleSyncService
{
    /// <summary>
    /// Provisions a new Google Drive folder for a team.
    /// </summary>
    /// <param name="teamId">The team ID.</param>
    /// <param name="folderName">The folder name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created Google resource.</returns>
    Task<GoogleResource> ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unified sync entry point. Computes diff for all active resources of the given type,
    /// then optionally executes adds/removes based on the action.
    /// Used by preview, manual actions, and scheduled jobs.
    /// </summary>
    Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sync a single resource. Same logic as SyncResourcesByTypeAsync but for one resource.
    /// </summary>
    Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of a Google resource.
    /// </summary>
    /// <param name="resourceId">The Google resource ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resource if found.</returns>
    Task<GoogleResource?> GetResourceStatusAsync(Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user to all Google resources associated with a team.
    /// </summary>
    /// <param name="teamId">The team ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddUserToTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from all Google resources associated with a team.
    /// </summary>
    /// <param name="teamId">The team ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveUserFromTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a team has a linked Google Group. If GoogleGroupPrefix is set but no Group
    /// resource exists, creates or links the group. Called when prefix is set on a team.
    /// </summary>
    Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisions a new Google Group for a team.
    /// </summary>
    /// <param name="teamId">The team ID.</param>
    /// <param name="groupEmail">The group email address (e.g., team-name@nobodies.team).</param>
    /// <param name="groupName">Display name for the group.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created Google resource.</returns>
    Task<GoogleResource> ProvisionTeamGroupAsync(
        Guid teamId,
        string groupEmail,
        string groupName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user to a Google Group.
    /// </summary>
    /// <param name="groupResourceId">The Google resource ID of the group.</param>
    /// <param name="userEmail">The user's email address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddUserToGroupAsync(Guid groupResourceId, string userEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from a Google Group.
    /// </summary>
    /// <param name="groupResourceId">The Google resource ID of the group.</param>
    /// <param name="userEmail">The user's email address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveUserFromGroupAsync(Guid groupResourceId, string userEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs all members of a team to its associated Google Group.
    /// </summary>
    /// <param name="teamId">The team ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SyncTeamGroupMembersAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a user to all their team-related Google resources.
    /// Used when a user returns to Active status (e.g., after signing documents).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RestoreUserToAllTeamsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks all active Google Groups for settings drift against the expected configuration.
    /// Detect-only: does not modify any settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Drift results for all groups, or a skipped result if sync is disabled.</returns>
    Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares stored user emails against the canonical emails from Google Admin SDK.
    /// Returns a list of users whose stored email differs from what Google reports.
    /// Detect-only: does not modify any data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Mismatch results for all users checked.</returns>
    Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies expected settings to a Google Group, fixing any drift.
    /// Respects SyncSettings mode — returns without action if sync is disabled.
    /// </summary>
    Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all Google Groups on the domain and cross-references with the local database.
    /// Returns drift status for each group relative to the expected settings.
    /// </summary>
    Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the current Drive folder path for each active Drive resource
    /// and updates GoogleResource.Name when the folder has been moved or renamed.
    /// Called during nightly reconciliation.
    /// </summary>
    /// <returns>Number of resources whose name was updated.</returns>
    Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets inheritedPermissionsDisabled on a Google Drive folder.
    /// When restrict is true, disables inherited permissions; when false, re-enables them.
    /// </summary>
    /// <param name="googleFileId">The Google Drive file/folder ID.</param>
    /// <param name="restrict">True to disable inheritance, false to enable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetInheritedPermissionsDisabledAsync(string googleFileId, bool restrict, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks and corrects inherited access drift for all Drive folders that have
    /// RestrictInheritedAccess enabled. Returns the number of folders corrected.
    /// </summary>
    Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of unprocessed Google sync outbox events that have a
    /// non-null <c>LastError</c>. Used by the notification meter to surface
    /// failed sync events to Admin without letting the Notifications section
    /// read <c>google_sync_outbox_events</c> directly (design-rules §2c).
    /// </summary>
    Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default);
}
