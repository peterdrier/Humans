using Google.Apis.Drive.v3.Data;

namespace Humans.Infrastructure.GoogleSync;

/// <summary>
/// Narrow seam over <see cref="global::Google.Apis.Drive.v3.DriveService"/> used by
/// <see cref="Services.GoogleWorkspaceSyncService"/>'s reconciliation + gateway paths.
/// Exists so the reconciliation tests can substitute an in-memory fake without touching the
/// SDK's sealed concrete types. Only the operations the reconciliation path uses are exposed.
/// Always targets Shared Drives (<c>SupportsAllDrives = true</c>) and requests
/// <c>permissionDetails</c> so callers can distinguish direct from inherited permissions.
/// </summary>
internal interface IGoogleDrivePermissionClient
{
    /// <summary>
    /// Returns all permissions on the given Drive file or folder id, including inherited
    /// ones on Shared Drives. Pagination is handled internally.
    /// </summary>
    Task<IReadOnlyList<Permission>> ListPermissionsAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a direct user permission on the given file id and returns the new permission id.
    /// <paramref name="role"/> is the Drive API role string (e.g. <c>reader</c>, <c>writer</c>,
    /// <c>fileOrganizer</c>).
    /// </summary>
    Task<string> CreatePermissionAsync(string fileId, string userEmail, string role, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the permission identified by <paramref name="permissionId"/> from the given file.
    /// </summary>
    Task DeletePermissionAsync(string fileId, string permissionId, CancellationToken cancellationToken);
}
