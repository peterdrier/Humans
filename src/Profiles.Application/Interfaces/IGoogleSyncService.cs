using Profiles.Domain.Entities;
using Profiles.Domain.Enums;

namespace Profiles.Application.Interfaces;

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
    /// Provisions a new Google Drive folder for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="folderName">The folder name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created Google resource.</returns>
    Task<GoogleResource> ProvisionUserFolderAsync(
        Guid userId,
        string folderName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs access permissions for a Google resource.
    /// </summary>
    /// <param name="resourceId">The Google resource ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SyncResourcePermissionsAsync(Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs all Google resources.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SyncAllResourcesAsync(CancellationToken cancellationToken = default);

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
}
