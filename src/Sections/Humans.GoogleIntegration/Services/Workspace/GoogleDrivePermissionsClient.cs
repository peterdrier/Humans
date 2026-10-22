using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Humans.Base.Extensions;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;
using SdkFile = Google.Apis.Drive.v3.Data.File;
using SdkPermission = Google.Apis.Drive.v3.Data.Permission;

namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>
/// Every request sets <c>SupportsAllDrives</c> (Shared Drives only); permission lists request
/// <c>permissionDetails</c> to tell inherited from direct.
/// </summary>
internal sealed class GoogleDrivePermissionsClient(
    IOptions<GoogleWorkspaceSettings> settings,
    ILogger<GoogleDrivePermissionsClient> logger) : IGoogleDrivePermissionsClient
{
    private const string PermissionListFields =
        "nextPageToken, permissions(id, emailAddress, role, type, permissionDetails)";
    private const string FileFields = "id, name, parents, driveId, inheritedPermissionsDisabled";

    private readonly GoogleWorkspaceSettings _settings = settings.Value;

    private DriveService? _driveService;

    public async Task<DrivePermissionListResult> ListPermissionsAsync(
        string fileId,
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
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
            logger.LogWarning(ex,
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
        using var _ = logger.TimeOperation();
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
            // A 400 with an "already exists" reason: the user already has the permission. Other 400s fall
            // through to the generic handler.
            return new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.AlreadyExists,
                Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            // Debug only: GoogleWorkspaceSyncService logs the failure with resource and role context.
            logger.LogDebug(ex,
                "Google API error granting {Role} to {Email} on {FileId}: Code={Code} Message={Message}",
                role, userEmail, fileId, ex.Error?.Code, ex.Error?.Message);
            return new DrivePermissionMutationResult(
                DrivePermissionCreateOutcome.Failed,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<GoogleClientError?> UpdatePermissionAsync(
        string fileId,
        string permissionId,
        string role,
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Permissions.Update(new SdkPermission { Role = role }, fileId, permissionId);
            request.SupportsAllDrives = true;
            await request.ExecuteAsync(ct);
            return null;
        }
        catch (Google.GoogleApiException ex)
        {
            logger.LogDebug(ex,
                "Google API error updating permission {PermissionId} to {Role} on {FileId}: Code={Code} Message={Message}",
                permissionId, role, fileId, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    public async Task<DrivePermissionDeleteResult> DeletePermissionAsync(
        string fileId,
        string permissionId,
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Permissions.Delete(fileId, permissionId);
            request.SupportsAllDrives = true;
            await request.ExecuteAsync(ct);
            return new DrivePermissionDeleteResult(DrivePermissionDeleteOutcome.Deleted, Error: null);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 403 && IsInheritedPermissionError(ex.Error))
        {
            // A 403 means the permission is inherited and can never be deleted here. Terminal, so the sync
            // stops re-attempting it every night.
            return new DrivePermissionDeleteResult(
                DrivePermissionDeleteOutcome.InheritedPermission,
                new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
        catch (Google.GoogleApiException ex)
        {
            // Debug only: GoogleWorkspaceSyncService logs the failure with resource context.
            logger.LogDebug(ex,
                "Google API error deleting permission {PermissionId} on {FileId}: Code={Code} Message={Message}",
                permissionId, fileId, ex.Error?.Code, ex.Error?.Message);
            return new DrivePermissionDeleteResult(
                DrivePermissionDeleteOutcome.Failed,
                new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<DriveFileMetadataResult> GetFileAsync(
        string fileId,
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
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
            logger.LogDebug(ex,
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
        using var _ = logger.TimeOperation();
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
            logger.LogWarning(ex,
                "Google API error setting inheritedPermissionsDisabled={Disabled} on {FileId}: Code={Code} Message={Message}",
                disabled, fileId, ex.Error?.Code, ex.Error?.Message);
            return new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message);
        }
    }

    public async Task<SharedDriveMetadataResult> GetSharedDriveAsync(
        string driveId,
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
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
            logger.LogDebug(ex,
                "Google API error reading Shared Drive {DriveId}: Code={Code} Message={Message}",
                driveId, ex.Error?.Code, ex.Error?.Message);
            return new SharedDriveMetadataResult(
                Drive: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    public async Task<DriveFolderCreateResult> CreateFolderAsync(
        string parentFolderId,
        string name,
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var metadata = new SdkFile
            {
                Name = name,
                MimeType = "application/vnd.google-apps.folder",
                Parents = [parentFolderId]
            };

            var request = drive.Files.Create(metadata);
            request.SupportsAllDrives = true;
            request.Fields = "id";
            var file = await request.ExecuteAsync(ct);
            return new DriveFolderCreateResult(file.Id, Error: null);
        }
        catch (Google.GoogleApiException ex)
        {
            logger.LogWarning(ex,
                "Google API error creating folder '{Name}' under {ParentFolderId}: Code={Code} Message={Message}",
                name, parentFolderId, ex.Error?.Code, ex.Error?.Message);
            return new DriveFolderCreateResult(
                FolderId: null,
                Error: new GoogleClientError(ex.Error?.Code ?? 0, ex.Error?.Message));
        }
    }

    /// <summary>
    /// A duplicate permission (safe as idempotent success) vs any other 400. Matches "already exists" in the message and
    /// each <c>errors[]</c> entry; anything unmatched is a failure, so real problems are not swallowed.
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

    /// <summary>
    /// An inherited permission (terminal: https://developers.google.com/workspace/drive/api/guides/limited-expansive-access)
    /// vs any other 403. Matches <c>cannotDeletePermission</c> and "inherited"; anything unmatched is a failure.
    /// </summary>
    internal static bool IsInheritedPermissionError(Google.Apis.Requests.RequestError error)
    {
        if (ContainsInherited(error.Message))
        {
            return true;
        }

        if (error.Errors is not null)
        {
            foreach (var detail in error.Errors)
            {
                if (ContainsInherited(detail.Message))
                {
                    return true;
                }

                if (string.Equals(detail.Reason, "cannotDeletePermission", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsInherited(string? text) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains("inherited", StringComparison.OrdinalIgnoreCase);

    private static DrivePermission MapPermission(SdkPermission p)
    {
        // ANY inherited permissionDetails entry makes the permission undeletable here, even with a direct
        // component.
        var hasInheritedComponent =
            p.PermissionDetails is { Count: > 0 } details &&
            details.Any(d => d.Inherited == true);

        return new DrivePermission(
            Id: p.Id,
            Type: p.Type,
            Role: p.Role,
            EmailAddress: p.EmailAddress,
            HasInheritedComponent: hasInheritedComponent)
        {
            HasDirectComponent = p.PermissionDetails is not { Count: > 0 } ||
                p.PermissionDetails.Any(d => d.Inherited == false),
            InheritedRoles = p.PermissionDetails?
                .Where(d => d.Inherited == true)
                .Select(d => d.Role)
                .ToArray() ?? []
        };
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
