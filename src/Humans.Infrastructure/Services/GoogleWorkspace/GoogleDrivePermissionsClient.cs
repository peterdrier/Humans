using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SdkFile = Google.Apis.Drive.v3.Data.File;
using SdkPermission = Google.Apis.Drive.v3.Data.Permission;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Real Google-backed implementation of <see cref="IGoogleDrivePermissionsClient"/>.
/// Talks to the Google Drive v3 API using the configured service account.
/// This is the only file that imports <c>Google.Apis.*</c> for Drive
/// folder and permission management performed by
/// <c>GoogleWorkspaceSyncService</c>; the Application-layer sync service
/// (coming in §15 Part 2b) never sees SDK types.
/// </summary>
/// <remarks>
/// Every request is issued with <c>SupportsAllDrives = true</c> per the
/// "Shared Drives only" rule. Permission list requests explicitly request
/// <c>permissionDetails</c> so callers can distinguish inherited from
/// direct permissions.
/// </remarks>
public sealed class GoogleDrivePermissionsClient : IGoogleDrivePermissionsClient
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string PermissionListFields =
        "nextPageToken, permissions(id, emailAddress, role, type, permissionDetails)";
    private const string FileFields = "id, name, parents, driveId, inheritedPermissionsDisabled";
    private const string CreateFolderFields = "id, name, webViewLink";

    private readonly GoogleWorkspaceSettings _settings;
    private readonly ILogger<GoogleDrivePermissionsClient> _logger;

    private DriveService? _driveService;

    public GoogleDrivePermissionsClient(
        IOptions<GoogleWorkspaceSettings> settings,
        ILogger<GoogleDrivePermissionsClient> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<DriveFolderCreateResult> CreateFolderAsync(
        string folderName,
        string? parentFolderId,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var metadata = new SdkFile
            {
                Name = folderName,
                MimeType = FolderMimeType
            };

            if (!string.IsNullOrEmpty(parentFolderId))
            {
                metadata.Parents = [parentFolderId];
            }

            var request = drive.Files.Create(metadata);
            request.Fields = CreateFolderFields;
            request.SupportsAllDrives = true;
            var folder = await request.ExecuteAsync(ct);

            return new DriveFolderCreateResult(
                new DriveFolder(folder.Id, folder.Name, folder.WebViewLink),
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error creating folder '{Name}' under parent {ParentId}: Code={Code} Message={Message}",
                folderName, parentFolderId ?? "(none)", ex.Error?.Code, ex.Error?.Message);
            return new DriveFolderCreateResult(
                Folder: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<DrivePermissionListResult> ListPermissionsAsync(
        string fileId,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var permissions = new List<DrivePermission>();
            string? pageToken = null;

            do
            {
                var request = drive.Permissions.List(fileId);
                request.SupportsAllDrives = true;
                request.Fields = PermissionListFields;
                if (pageToken is not null)
                {
                    request.PageToken = pageToken;
                }

                var response = await request.ExecuteAsync(ct);

                if (response.Permissions is not null)
                {
                    foreach (var p in response.Permissions)
                    {
                        permissions.Add(MapPermission(p));
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            return new DrivePermissionListResult(permissions, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error listing permissions for {FileId}: Code={Code} Message={Message}",
                fileId, ex.Error?.Code, ex.Error?.Message);
            return new DrivePermissionListResult(
                Permissions: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<DrivePermissionMutationResult> CreatePermissionAsync(
        string fileId,
        string userEmail,
        string role,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var permission = new SdkPermission
            {
                Type = "user",
                Role = role,
                EmailAddress = userEmail
            };

            var request = drive.Permissions.Create(permission, fileId);
            request.SupportsAllDrives = true;
            request.SendNotificationEmail = false;
            await request.ExecuteAsync(ct);

            return new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Created,
                Error: null);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 400 && IsDuplicatePermissionError(ex.Error))
        {
            // Drive returns HTTP 400 with an "already exists"-style reason
            // when the same user already has a permission on the file.
            // Other 400s (malformed payload, unsupported role, etc.) are
            // real failures and fall through to the generic handler below
            // so callers can surface and retry them.
            return new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.AlreadyExists,
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error granting {Role} to {Email} on {FileId}: Code={Code} Message={Message}",
                role, userEmail, fileId, ex.Error?.Code, ex.Error?.Message);
            return new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GoogleClientError?> DeletePermissionAsync(
        string fileId,
        string permissionId,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Permissions.Delete(fileId, permissionId);
            request.SupportsAllDrives = true;
            await request.ExecuteAsync(ct);
            return null;
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error deleting permission {PermissionId} on {FileId}: Code={Code} Message={Message}",
                permissionId, fileId, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    public async Task<DriveFileMetadataResult> GetFileAsync(
        string fileId,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Files.Get(fileId);
            request.SupportsAllDrives = true;
            request.Fields = FileFields;
            var file = await request.ExecuteAsync(ct);
            return new DriveFileMetadataResult(
                new DriveFileMetadata(
                    Id: file.Id,
                    Name: file.Name,
                    Parents: file.Parents as IReadOnlyList<string>,
                    DriveId: file.DriveId,
                    InheritedPermissionsDisabled: file.InheritedPermissionsDisabled),
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogDebug(ex,
                "Google API error reading file {FileId}: Code={Code} Message={Message}",
                fileId, ex.Error?.Code, ex.Error?.Message);
            return new DriveFileMetadataResult(
                File: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GoogleClientError?> SetInheritedPermissionsDisabledAsync(
        string fileId,
        bool disabled,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var metadata = new SdkFile
            {
                InheritedPermissionsDisabled = disabled
            };
            var request = drive.Files.Update(metadata, fileId);
            request.SupportsAllDrives = true;
            request.Fields = "id, inheritedPermissionsDisabled";
            await request.ExecuteAsync(ct);
            return null;
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex,
                "Google API error setting inheritedPermissionsDisabled={Disabled} on {FileId}: Code={Code} Message={Message}",
                disabled, fileId, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    public async Task<SharedDriveMetadataResult> GetSharedDriveAsync(
        string driveId,
        CancellationToken ct = default)
    {
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var info = await drive.Drives.Get(driveId).ExecuteAsync(ct);
            return new SharedDriveMetadataResult(
                new SharedDriveMetadata(info.Id, info.Name),
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogDebug(ex,
                "Google API error reading Shared Drive {DriveId}: Code={Code} Message={Message}",
                driveId, ex.Error?.Code, ex.Error?.Message);
            return new SharedDriveMetadataResult(
                Drive: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    /// <summary>
    /// Classifies an HTTP 400 error as a duplicate-permission case (safe to
    /// treat as idempotent success) vs. any other bad-request failure
    /// (malformed payload, invalid role, etc.). Matches Google's
    /// "already exists" wording in both the top-level message and each
    /// <c>errors[].message</c> / <c>errors[].reason</c> so that wording
    /// changes on one side still fall into the correct bucket. Any error
    /// that does not match defaults to Failed so real problems are not
    /// silently swallowed.
    /// </summary>
    internal static bool IsDuplicatePermissionError(Google.Apis.Requests.RequestError error)
    {
        if (ContainsAlreadyExists(error.Message))
        {
            return true;
        }

        if (error.Errors is not null)
        {
            foreach (var detail in error.Errors)
            {
                if (ContainsAlreadyExists(detail.Message))
                {
                    return true;
                }

                var reason = detail.Reason ?? string.Empty;
                if (string.Equals(reason, "duplicate", StringComparison.Ordinal) ||
                    string.Equals(reason, "alreadyExists", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsAlreadyExists(string? text) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains("already exist", StringComparison.OrdinalIgnoreCase);

    private static DrivePermission MapPermission(SdkPermission p)
    {
        var isInheritedOnly =
            p.PermissionDetails is { Count: > 0 } details &&
            details.All(d => d.Inherited == true);

        return new DrivePermission(
            Id: p.Id,
            Type: p.Type,
            Role: p.Role,
            EmailAddress: p.EmailAddress,
            IsInheritedOnly: isInheritedOnly);
    }

    private async Task<DriveService> GetDriveServiceAsync(CancellationToken ct)
    {
        if (_driveService is not null)
        {
            return _driveService;
        }

        var credential = await GoogleCredentialLoader
            .LoadScopedAsync(_settings, ct, DriveService.Scope.Drive);

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _driveService;
    }
}
