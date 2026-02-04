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
}
