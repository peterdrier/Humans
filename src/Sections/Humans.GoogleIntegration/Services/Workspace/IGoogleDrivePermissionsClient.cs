namespace Humans.GoogleIntegration.Services.Workspace;

internal interface IGoogleDrivePermissionsClient
{
    /// <summary>Direct and inherited permissions both.</summary>
    Task<DrivePermissionListResult> ListPermissionsAsync(
        string fileId,
        CancellationToken ct = default);

    /// <summary>
    /// Sends no notification email. A 400 "already exists" returns <see cref="DrivePermissionCreateOutcome.AlreadyExists"/>.
    /// </summary>
    Task<DrivePermissionMutationResult> CreatePermissionAsync(
        string fileId,
        string userEmail,
        string role,
        CancellationToken ct = default);

    /// <summary>
    /// Unlike delete and recreate, can lower a mixed permission's direct elevation to its inherited floor. Null on
    /// success.
    /// </summary>
    Task<GoogleClientError?> UpdatePermissionAsync(
        string fileId,
        string permissionId,
        string role,
        CancellationToken ct = default);

    /// <summary><see cref="DrivePermissionDeleteOutcome.InheritedPermission"/> is terminal: callers must not retry.</summary>
    Task<DrivePermissionDeleteResult> DeletePermissionAsync(
        string fileId,
        string permissionId,
        CancellationToken ct = default);

    Task<DriveFileMetadataResult> GetFileAsync(
        string fileId,
        CancellationToken ct = default);

    Task<GoogleClientError?> SetInheritedPermissionsDisabledAsync(
        string fileId,
        bool disabled,
        CancellationToken ct = default);

    Task<SharedDriveMetadataResult> GetSharedDriveAsync(
        string driveId,
        CancellationToken ct = default);

    Task<DriveFolderCreateResult> CreateFolderAsync(
        string parentFolderId,
        string name,
        CancellationToken ct = default);
}

/// <summary>Exactly one of <see cref="Permissions"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DrivePermissionListResult(
    IReadOnlyList<DrivePermission>? Permissions,
    GoogleClientError? Error);

/// <summary>
/// Fields are nullable because Google omits <c>role</c> on stale rows during maintenance. Only <c>user</c> permissions
/// are managed. <c>HasInheritedComponent</c> is true when ANY detail is inherited: Drive won't delete those.
/// </summary>
internal sealed record DrivePermission(
    string? Id,
    string? Type,
    string? Role,
    string? EmailAddress,
    bool HasInheritedComponent)
{
    /// <summary>Whether Google reports a grant made directly on this item.</summary>
    public bool HasDirectComponent { get; init; } = !HasInheritedComponent;

    /// <summary>The inherited role floor comes from these components, not the effective Role.</summary>
    public IReadOnlyList<string?> InheritedRoles { get; init; } = HasInheritedComponent ? [Role] : [];
}

internal sealed record DrivePermissionMutationResult(
    DrivePermissionCreateOutcome Outcome,
    GoogleClientError? Error);

internal enum DrivePermissionCreateOutcome
{
    Created,

    /// <summary>Google's 400 "already exists": idempotent success.</summary>
    AlreadyExists,

    /// <summary><see cref="DrivePermissionMutationResult.Error"/> is populated.</summary>
    Failed
}

internal sealed record DrivePermissionDeleteResult(
    DrivePermissionDeleteOutcome Outcome,
    GoogleClientError? Error);

internal enum DrivePermissionDeleteOutcome
{
    Deleted,

    /// <summary>Google's 403 for an inherited permission. Terminal: record it and stop retrying.</summary>
    InheritedPermission,

    /// <summary><see cref="DrivePermissionDeleteResult.Error"/> is populated.</summary>
    Failed
}

/// <summary>Exactly one of <see cref="File"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DriveFileMetadataResult(DriveFileMetadata? File, GoogleClientError? Error);

/// <summary><see cref="Name"/> is nullable: <c>Files.Get</c> on a Shared Drive root can omit it.</summary>
internal sealed record DriveFileMetadata(
    string? Id,
    string? Name,
    IReadOnlyList<string>? Parents,
    string? DriveId,
    bool? InheritedPermissionsDisabled);

/// <summary>Exactly one of <see cref="Drive"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record SharedDriveMetadataResult(SharedDriveMetadata? Drive, GoogleClientError? Error);

internal sealed record SharedDriveMetadata(string Id, string Name);

/// <summary>Exactly one of <see cref="FolderId"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DriveFolderCreateResult(string? FolderId, GoogleClientError? Error);
