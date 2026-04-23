using Humans.Application.Interfaces.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Dev/test <see cref="IGoogleDrivePermissionsClient"/> that keeps an
/// in-memory store of folders and permissions so the Application-layer sync
/// service can exercise Drive flows without a Google service account. Per
/// the §15 connector pattern, the Application-layer service runs against
/// this stub — there is no "stub service" variant.
/// </summary>
public sealed class StubGoogleDrivePermissionsClient : IGoogleDrivePermissionsClient
{
    private readonly ILogger<StubGoogleDrivePermissionsClient> _logger;
    private readonly Dictionary<string, StubFile> _filesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DrivePermission>> _permissionsByFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedDriveMetadata> _drivesById = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _nextFileId = 1;
    private long _nextPermissionId = 1;

    public StubGoogleDrivePermissionsClient(ILogger<StubGoogleDrivePermissionsClient> logger)
    {
        _logger = logger;
    }

    public Task<DriveFolderCreateResult> CreateFolderAsync(
        string folderName,
        string? parentFolderId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Create folder '{Name}' under parent {Parent}",
            folderName, parentFolderId ?? "(root)");

        lock (_gate)
        {
            var id = $"stubfolder-{_nextFileId++}";
            _filesById[id] = new StubFile(id, folderName, parentFolderId, DriveId: null, InheritedPermissionsDisabled: null);
            _permissionsByFile[id] = new List<DrivePermission>();
            var link = $"https://drive.google.com/drive/folders/{id}";
            return Task.FromResult(new DriveFolderCreateResult(
                new DriveFolder(id, folderName, link),
                Error: null));
        }
    }

    public Task<DrivePermissionListResult> ListPermissionsAsync(
        string fileId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] List permissions for {FileId}", fileId);

        lock (_gate)
        {
            if (!_permissionsByFile.TryGetValue(fileId, out var perms))
            {
                return Task.FromResult(new DrivePermissionListResult(
                    Permissions: Array.Empty<DrivePermission>(),
                    Error: null));
            }

            return Task.FromResult(new DrivePermissionListResult(
                perms.ToList(), Error: null));
        }
    }

    public Task<DrivePermissionMutationResult> CreatePermissionAsync(
        string fileId,
        string userEmail,
        string role,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Grant {Role} to {Email} on {FileId}", role, userEmail, fileId);

        lock (_gate)
        {
            if (!_permissionsByFile.TryGetValue(fileId, out var perms))
            {
                perms = new List<DrivePermission>();
                _permissionsByFile[fileId] = perms;
            }

            if (perms.Any(p =>
                string.Equals(p.Type, "user", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.EmailAddress, userEmail, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(new DrivePermissionMutationResult(
                    DrivePermissionCreateOutcome.AlreadyExists, Error: null));
            }

            var id = $"stubperm-{_nextPermissionId++}";
            perms.Add(new DrivePermission(
                Id: id,
                Type: "user",
                Role: role,
                EmailAddress: userEmail,
                IsInheritedOnly: false));

            return Task.FromResult(new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Created, Error: null));
        }
    }

    public Task<GoogleClientError?> DeletePermissionAsync(
        string fileId,
        string permissionId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Delete permission {PermId} from {FileId}", permissionId, fileId);

        lock (_gate)
        {
            if (!_permissionsByFile.TryGetValue(fileId, out var perms))
            {
                return Task.FromResult<GoogleClientError?>(new GoogleClientError(404, "file not found"));
            }

            var idx = perms.FindIndex(p => string.Equals(p.Id, permissionId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return Task.FromResult<GoogleClientError?>(new GoogleClientError(404, "permission not found"));
            }

            perms.RemoveAt(idx);
            return Task.FromResult<GoogleClientError?>(null);
        }
    }

    public Task<DriveFileMetadataResult> GetFileAsync(
        string fileId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] Get file {FileId}", fileId);

        lock (_gate)
        {
            if (!_filesById.TryGetValue(fileId, out var file))
            {
                return Task.FromResult(new DriveFileMetadataResult(
                    File: null,
                    Error: new GoogleClientError(404, "file not found")));
            }

            var parents = file.ParentId is null ? null : (IReadOnlyList<string>)[file.ParentId];
            return Task.FromResult(new DriveFileMetadataResult(
                new DriveFileMetadata(
                    Id: file.Id,
                    Name: file.Name,
                    Parents: parents,
                    DriveId: file.DriveId,
                    InheritedPermissionsDisabled: file.InheritedPermissionsDisabled),
                Error: null));
        }
    }

    public Task<GoogleClientError?> SetInheritedPermissionsDisabledAsync(
        string fileId,
        bool disabled,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Set inheritedPermissionsDisabled={Disabled} on {FileId}",
            disabled, fileId);

        lock (_gate)
        {
            if (!_filesById.TryGetValue(fileId, out var file))
            {
                return Task.FromResult<GoogleClientError?>(new GoogleClientError(404, "file not found"));
            }

            _filesById[fileId] = file with { InheritedPermissionsDisabled = disabled };
            return Task.FromResult<GoogleClientError?>(null);
        }
    }

    public Task<SharedDriveMetadataResult> GetSharedDriveAsync(
        string driveId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("[STUB] Get shared drive {DriveId}", driveId);

        lock (_gate)
        {
            if (_drivesById.TryGetValue(driveId, out var drive))
            {
                return Task.FromResult(new SharedDriveMetadataResult(drive, Error: null));
            }
        }

        return Task.FromResult(new SharedDriveMetadataResult(
            Drive: null,
            Error: new GoogleClientError(404, "shared drive not found")));
    }

    private sealed record StubFile(
        string Id,
        string Name,
        string? ParentId,
        string? DriveId,
        bool? InheritedPermissionsDisabled);
}
