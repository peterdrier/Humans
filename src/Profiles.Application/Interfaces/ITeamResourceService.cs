using Profiles.Application.DTOs;
using Profiles.Domain.Entities;

namespace Profiles.Application.Interfaces;

/// <summary>
/// Service for linking and managing pre-shared Google resources for teams.
/// Unlike IGoogleSyncService (which provisions new resources), this service
/// validates and links existing resources that have been pre-shared with the service account.
/// </summary>
public interface ITeamResourceService
{
    /// <summary>
    /// Gets all Google resources linked to a team.
    /// </summary>
    Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Links an existing Google Drive folder to a team by URL.
    /// The folder must be pre-shared with the service account as Editor.
    /// </summary>
    Task<LinkResourceResult> LinkDriveFolderAsync(Guid teamId, string folderUrl, CancellationToken ct = default);

    /// <summary>
    /// Links an existing Google Drive file (Sheet, Doc, etc.) to a team by URL.
    /// The file must be on a Shared Drive pre-shared with the service account.
    /// </summary>
    Task<LinkResourceResult> LinkDriveFileAsync(Guid teamId, string fileUrl, CancellationToken ct = default);

    /// <summary>
    /// Links a Google Drive resource (folder or file) to a team by URL.
    /// Automatically detects the resource type from the URL and routes accordingly.
    /// </summary>
    Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, CancellationToken ct = default);

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
    /// Checks whether a user can manage resources for a team.
    /// Board members can always manage. Metaleads can manage if the admin setting allows it.
    /// </summary>
    Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the service account email address for display in sharing instructions.
    /// </summary>
    Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default);
}
