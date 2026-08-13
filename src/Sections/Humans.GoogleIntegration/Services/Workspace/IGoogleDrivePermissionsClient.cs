using Humans.Application.Interfaces;
using Humans.GoogleIntegration.Services;
namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>
/// Narrow connector over the Google Drive v3 API scoped to the folder- and
/// permission-management operations performed by <c>GoogleWorkspaceSyncService</c>.
/// Implementations live in <c>Humans.Infrastructure</c>; the Application-layer
/// sync service (coming in §15 Part 2b, issue #575) depends only on this
/// interface so that <c>Humans.Application</c> stays free of
/// <c>Google.Apis.*</c> imports (design-rules §13).
/// </summary>
/// <remarks>
/// All Drive operations run with <c>SupportsAllDrives = true</c> per the
/// "Shared Drives only" rule in <c>CLAUDE.md</c>. Permission lists always
/// include <c>permissionDetails</c> so callers can distinguish inherited
/// from direct permissions.
/// </remarks>
internal interface IGoogleDrivePermissionsClient
{
    /// <summary>
    /// Creates a new folder under <paramref name="parentFolderId"/> (when
    /// provided) with the given <paramref name="folderName"/> and returns its
    /// Google-assigned id, final name, and web-view link.
    /// </summary>
    Task<DriveFolderCreateResult> CreateFolderAsync(
        string folderName,
        string? parentFolderId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists every permission on the Drive item identified by
    /// <paramref name="fileId"/>, paginating internally. The returned list
    /// includes both direct and inherited permissions so callers can decide
    /// which to act on.
    /// </summary>
    Task<DrivePermissionListResult> ListPermissionsAsync(
        string fileId,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a user-type permission on <paramref name="fileId"/> granting
    /// <paramref name="role"/> to <paramref name="userEmail"/>. The
    /// implementation sets <c>SendNotificationEmail = false</c> so no email
    /// is sent — the system surfaces access through its own UI.
    /// </summary>
    /// <returns>
    /// <see cref="DrivePermissionCreateOutcome.Created"/> on success,
    /// <see cref="DrivePermissionCreateOutcome.AlreadyExists"/> when Google
    /// responded with HTTP 400 indicating the permission already exists
    /// (idempotent), or a populated <see cref="GoogleClientError"/>.
    /// </returns>
    Task<DrivePermissionMutationResult> CreatePermissionAsync(
        string fileId,
        string userEmail,
        string role,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the permission identified by <paramref name="permissionId"/>
    /// from <paramref name="fileId"/>.
    /// </summary>
    /// <returns>
    /// <see cref="DrivePermissionDeleteOutcome.Deleted"/> on success,
    /// <see cref="DrivePermissionDeleteOutcome.InheritedPermission"/> when
    /// Google responded with HTTP 403 because the permission is inherited
    /// from a parent folder and cannot be deleted at this level (terminal —
    /// callers must not retry), or <see cref="DrivePermissionDeleteOutcome.Failed"/>
    /// with a populated <see cref="GoogleClientError"/> for any other error.
    /// </returns>
    Task<DrivePermissionDeleteResult> DeletePermissionAsync(
        string fileId,
        string permissionId,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the shape-neutral metadata for the Drive file identified by
    /// <paramref name="fileId"/>. Used during path resolution and
    /// inheritance-restriction enforcement.
    /// </summary>
    Task<DriveFileMetadataResult> GetFileAsync(
        string fileId,
        CancellationToken ct = default);

    /// <summary>
    /// Updates <paramref name="fileId"/>'s <c>inheritedPermissionsDisabled</c>
    /// bit. Used by the reconciliation job to re-assert access restrictions
    /// when drift is detected.
    /// </summary>
    Task<GoogleClientError?> SetInheritedPermissionsDisabledAsync(
        string fileId,
        bool disabled,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the Shared Drive's display name for the given
    /// <paramref name="driveId"/>. Used when building the human-readable
    /// folder path.
    /// </summary>
    Task<SharedDriveMetadataResult> GetSharedDriveAsync(
        string driveId,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IGoogleDrivePermissionsClient.CreateFolderAsync"/>.
/// Exactly one of <see cref="Folder"/> or <see cref="Error"/> is non-null.
/// </summary>
internal sealed record DriveFolderCreateResult(DriveFolder? Folder, GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of a newly-created Drive folder. <see cref="Id"/>
/// and <see cref="Name"/> are nullable to match the underlying SDK's own
/// nullability; on success Google always populates them.
/// </summary>
internal sealed record DriveFolder(string? Id, string? Name, string? WebViewLink);

/// <summary>
/// Outcome of <see cref="IGoogleDrivePermissionsClient.ListPermissionsAsync"/>.
/// Exactly one of <see cref="Permissions"/> or <see cref="Error"/> is non-null.
/// </summary>
internal sealed record DrivePermissionListResult(
    IReadOnlyList<DrivePermission>? Permissions,
    GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of a Drive permission row. Fields mirror the
/// subset the sync service needs: who, what role, whether it's inherited.
/// All identity/role fields are nullable because the Drive SDK's own types
/// are nullable — Google has been observed to omit <c>role</c> on stale
/// rows during maintenance windows, so callers must be resilient.
/// </summary>
/// <param name="Id">Drive's permission id (used as the key for delete). Null on the rare malformed row.</param>
/// <param name="Type">
/// <c>user</c>, <c>group</c>, <c>domain</c>, or <c>anyone</c>. Only <c>user</c>
/// permissions are managed by this system.
/// </param>
/// <param name="Role">
/// Google role name — <c>reader</c>, <c>commenter</c>, <c>writer</c>,
/// <c>fileOrganizer</c>, <c>organizer</c>, or <c>owner</c>.
/// </param>
/// <param name="EmailAddress">
/// The granted user's email. Null for non-user permissions (domain, anyone).
/// </param>
/// <param name="HasInheritedComponent">
/// True when ANY entry in <c>permissionDetails</c> is marked inherited — not
/// only when every entry is. Issue nobodies-collective/Humans#945: a
/// permission can carry both a direct and an inherited
/// <c>permissionDetails</c> entry (e.g. the same role granted directly on
/// this item and also inherited from a parent folder), and Drive's
/// <c>permissions.delete</c> still 403s on those as "cannot delete an
/// inherited permission." Only a permission with zero inherited components
/// is safely deletable at this level — the system must skip any permission
/// with an inherited component during reconciliation, not just fully-
/// inherited ones.
/// </param>
internal sealed record DrivePermission(
    string? Id,
    string? Type,
    string? Role,
    string? EmailAddress,
    bool HasInheritedComponent);

/// <summary>
/// Outcome of <see cref="IGoogleDrivePermissionsClient.CreatePermissionAsync"/>.
/// </summary>
internal sealed record DrivePermissionMutationResult(
    DrivePermissionCreateOutcome Outcome,
    GoogleClientError? Error);

/// <summary>
/// What happened when creating a Drive permission.
/// </summary>
internal enum DrivePermissionCreateOutcome
{
    /// <summary>The permission was newly created.</summary>
    Created,

    /// <summary>Google responded with HTTP 400 indicating the permission already existed. Treat as success (idempotent).</summary>
    AlreadyExists,

    /// <summary>Google responded with any other error. <see cref="DrivePermissionMutationResult.Error"/> is populated.</summary>
    Failed
}

/// <summary>
/// Outcome of <see cref="IGoogleDrivePermissionsClient.DeletePermissionAsync"/>.
/// </summary>
internal sealed record DrivePermissionDeleteResult(
    DrivePermissionDeleteOutcome Outcome,
    GoogleClientError? Error);

/// <summary>
/// What happened when deleting a Drive permission.
/// </summary>
internal enum DrivePermissionDeleteOutcome
{
    /// <summary>The permission was deleted.</summary>
    Deleted,

    /// <summary>
    /// Google responded with HTTP 403 because the permission is inherited
    /// from a parent folder — it cannot be deleted at this level. Terminal:
    /// the caller must record the outcome and stop retrying
    /// (nobodies-collective/Humans#945).
    /// </summary>
    InheritedPermission,

    /// <summary>Google responded with any other error. <see cref="DrivePermissionDeleteResult.Error"/> is populated.</summary>
    Failed
}

/// <summary>
/// Outcome of <see cref="IGoogleDrivePermissionsClient.GetFileAsync"/>.
/// Exactly one of <see cref="File"/> or <see cref="Error"/> is non-null.
/// </summary>
internal sealed record DriveFileMetadataResult(DriveFileMetadata? File, GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of Drive file metadata, limited to the fields
/// the sync service needs for path walking and inheritance enforcement.
/// <see cref="Id"/> and <see cref="Name"/> are nullable to match the
/// underlying SDK's nullability — a successful <c>Files.Get</c> against
/// a Shared Drive root has been observed to omit the name field.
/// </summary>
internal sealed record DriveFileMetadata(
    string? Id,
    string? Name,
    IReadOnlyList<string>? Parents,
    string? DriveId,
    bool? InheritedPermissionsDisabled);

/// <summary>
/// Outcome of <see cref="IGoogleDrivePermissionsClient.GetSharedDriveAsync"/>.
/// Exactly one of <see cref="Drive"/> or <see cref="Error"/> is non-null.
/// </summary>
internal sealed record SharedDriveMetadataResult(SharedDriveMetadata? Drive, GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of a Shared Drive's metadata — currently just
/// its display name.
/// </summary>
internal sealed record SharedDriveMetadata(string Id, string Name);
