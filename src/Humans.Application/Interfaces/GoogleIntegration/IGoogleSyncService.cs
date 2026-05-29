using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Service for provisioning and syncing Google resources. The cross-section
/// read projections (outbox reads) live on
/// <see cref="IGoogleSyncServiceRead"/>; this full surface adds the
/// provisioning and sync mutations consumed inside the section.
/// </summary>
public interface IGoogleSyncService : IGoogleSyncServiceRead, IApplicationService
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
    /// Drive sync entry point. Computes diff for all active Drive resources of the given
    /// type, then optionally executes adds/removes based on the action. Google Group
    /// membership is handled by <see cref="IGoogleGroupSync"/>.
    /// </summary>
    Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs a single Google resource by ID. Drive resources are reconciled here;
    /// Google Group resources are routed through <see cref="IGoogleGroupSync"/>.
    /// </summary>
    Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default);

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
    /// Checks all active Google Groups for settings drift against the expected configuration.
    /// Detect-only: does not modify any settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Drift results for all groups, or a skipped result if sync is disabled.</returns>
    Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies expected settings to a Google Group, fixing any drift.
    /// Respects SyncSettings mode — returns without action if sync is disabled.
    /// </summary>
    Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default);

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
}

public sealed record GroupSettingsRemediationResult(bool Succeeded, string? ErrorMessage)
{
    public static GroupSettingsRemediationResult Success() => new(true, null);

    public static GroupSettingsRemediationResult Failure(string message) => new(false, message);
}
