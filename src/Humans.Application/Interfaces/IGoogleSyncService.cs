using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for provisioning and syncing Google resources.
///
/// All methods that call the Google Workspace API require a <see cref="ClaimsPrincipal"/>
/// so authorization can be enforced at the service boundary before any external
/// side effects. Background jobs must pass
/// <see cref="Humans.Application.Authorization.SystemPrincipal.Instance"/>.
/// </summary>
public interface IGoogleSyncService
{
    /// <summary>
    /// Provisions a new Google Drive folder for a team.
    /// </summary>
    Task<GoogleResource> ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unified sync entry point. Computes diff for all active resources of the given type,
    /// then optionally executes adds/removes based on the action.
    /// Used by preview, manual actions, and scheduled jobs.
    /// </summary>
    Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sync a single resource. Same logic as SyncResourcesByTypeAsync but for one resource.
    /// </summary>
    Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of a Google resource. Does not call the Google API — safe to
    /// call without a principal.
    /// </summary>
    Task<GoogleResource?> GetResourceStatusAsync(Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user to all Google resources associated with a team.
    /// </summary>
    Task AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from all Google resources associated with a team.
    /// </summary>
    Task RemoveUserFromTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a team has a linked Google Group. If GoogleGroupPrefix is set but no Group
    /// resource exists, creates or links the group. Called when prefix is set on a team.
    /// </summary>
    Task<GroupLinkResult> EnsureTeamGroupAsync(
        Guid teamId,
        ClaimsPrincipal principal,
        bool confirmReactivation = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisions a new Google Group for a team.
    /// </summary>
    Task<GoogleResource> ProvisionTeamGroupAsync(
        Guid teamId,
        string groupEmail,
        string groupName,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user to a Google Group.
    /// </summary>
    Task AddUserToGroupAsync(
        Guid groupResourceId,
        string userEmail,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from a Google Group.
    /// </summary>
    Task RemoveUserFromGroupAsync(
        Guid groupResourceId,
        string userEmail,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs all members of a team to its associated Google Group.
    /// </summary>
    Task SyncTeamGroupMembersAsync(
        Guid teamId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a user to all their team-related Google resources.
    /// Used when a user returns to Active status (e.g., after signing documents).
    /// </summary>
    Task RestoreUserToAllTeamsAsync(
        Guid userId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks all active Google Groups for settings drift against the expected configuration.
    /// Detect-only: does not modify any settings.
    /// </summary>
    Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares stored user emails against the canonical emails from Google Admin SDK.
    /// Returns a list of users whose stored email differs from what Google reports.
    /// Detect-only: does not modify any data.
    /// </summary>
    Task<EmailBackfillResult> GetEmailMismatchesAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies expected settings to a Google Group, fixing any drift.
    /// Respects SyncSettings mode — returns without action if sync is disabled.
    /// </summary>
    Task<bool> RemediateGroupSettingsAsync(
        string groupEmail,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all Google Groups on the domain and cross-references with the local database.
    /// Returns drift status for each group relative to the expected settings.
    /// </summary>
    Task<AllGroupsResult> GetAllDomainGroupsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the current Drive folder path for each active Drive resource
    /// and updates GoogleResource.Name when the folder has been moved or renamed.
    /// Called during nightly reconciliation.
    /// </summary>
    Task<int> UpdateDriveFolderPathsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets inheritedPermissionsDisabled on a Google Drive folder.
    /// When restrict is true, disables inherited permissions; when false, re-enables them.
    /// </summary>
    Task SetInheritedPermissionsDisabledAsync(
        string googleFileId,
        bool restrict,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks and corrects inherited access drift for all Drive folders that have
    /// RestrictInheritedAccess enabled. Returns the number of folders corrected.
    /// </summary>
    Task<int> EnforceInheritedAccessRestrictionsAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}
