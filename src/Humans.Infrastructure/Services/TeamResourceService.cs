using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.CloudIdentity.v1.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Manages linking pre-shared Google resources to teams.
/// Uses service account credentials WITHOUT impersonation to validate access.
/// </summary>
public partial class TeamResourceService : ITeamResourceService
{
    private readonly HumansDbContext _dbContext;
    private readonly GoogleWorkspaceSettings _googleSettings;
    private readonly TeamResourceManagementSettings _resourceSettings;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;
    private readonly ILogger<TeamResourceService> _logger;

    private DriveService? _driveService;
    private CloudIdentityService? _cloudIdentityService;
    private string? _serviceAccountEmail;

    public TeamResourceService(
        HumansDbContext dbContext,
        IOptions<GoogleWorkspaceSettings> googleSettings,
        IOptions<TeamResourceManagementSettings> resourceSettings,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock,
        ILogger<TeamResourceService> logger)
    {
        _dbContext = dbContext;
        _googleSettings = googleSettings.Value;
        _resourceSettings = resourceSettings.Value;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleResource>> GetTeamResourcesAsync(Guid teamId, CancellationToken ct = default)
    {
        return await TeamResourcePersistence.GetActiveTeamResourcesAsync(_dbContext, teamId, ct);
    }

    /// <inheritdoc />
    public async Task<LinkResourceResult> LinkDriveFolderAsync(Guid teamId, string folderUrl, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default)
    {
        var folderId = ParseDriveFolderId(folderUrl);
        if (folderId is null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "Invalid Google Drive folder URL. Please use a URL like https://drive.google.com/drive/folders/...");
        }

        // Check if this folder is already linked to this team
        var existing = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && r.GoogleId == folderId
                && r.ResourceType == GoogleResourceType.DriveFolder
                && r.IsActive, ct);

        if (existing is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Drive folder is already linked to this team.");
        }

        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Files.Get(folderId);
            request.Fields = "id, name, webViewLink, mimeType, parents, driveId";
            request.SupportsAllDrives = true;
            var file = await request.ExecuteAsync(ct);

            if (!string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            {
                return new LinkResourceResult(false,
                    ErrorMessage: "The provided URL does not point to a Google Drive folder.");
            }

            var folderPath = await BuildFolderPathAsync(drive, file, ct);
            var now = _clock.GetCurrentInstant();

            // Check for an inactive record to reactivate
            var inactive = await _dbContext.GoogleResources
                .FirstOrDefaultAsync(r => r.TeamId == teamId
                    && r.GoogleId == folderId
                    && r.ResourceType == GoogleResourceType.DriveFolder
                    && !r.IsActive, ct);

            GoogleResource resource;
            if (inactive is not null)
            {
                inactive.Name = folderPath;
                inactive.Url = file.WebViewLink;
                inactive.LastSyncedAt = now;
                inactive.IsActive = true;
                inactive.ErrorMessage = null;
                resource = inactive;
            }
            else
            {
                resource = new GoogleResource
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    ResourceType = GoogleResourceType.DriveFolder,
                    GoogleId = file.Id,
                    Name = folderPath,
                    Url = file.WebViewLink,
                    ProvisionedAt = now,
                    LastSyncedAt = now,
                    IsActive = true,
                    DrivePermissionLevel = permissionLevel
                };
                _dbContext.GoogleResources.Add(resource);
            }

            if (inactive is not null)
            {
                inactive.DrivePermissionLevel = permissionLevel;
            }

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Linked Drive folder {FolderId} ({FolderName}) to team {TeamId} with permission {Permission}",
                file.Id, file.Name, teamId, permissionLevel);

            return new LinkResourceResult(true, Resource: resource);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google API error linking Drive folder {FolderId}: Code={Code} Message={Message}",
                folderId, ex.Error?.Code, ex.Error?.Message);
            var serviceAccountEmail = await GetServiceAccountEmailAsync(ct);
            var hint = ex.Error?.Code switch
            {
                404 => "The folder was not found or the service account does not have access.",
                403 => "The service account does not have permission to access this folder.",
                _ => $"Google API error ({ex.Error?.Code}): {ex.Error?.Message}"
            };
            return new LinkResourceResult(false,
                ErrorMessage: $"{hint} Please share the folder with the service account as Contributor.",
                ServiceAccountEmail: serviceAccountEmail);
        }
    }

    /// <inheritdoc />
    public async Task<LinkResourceResult> LinkDriveFileAsync(Guid teamId, string fileUrl, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default)
    {
        var fileId = ParseDriveFileId(fileUrl);
        if (fileId is null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "Invalid Google Drive file URL. Please use a URL like https://docs.google.com/spreadsheets/d/... or https://drive.google.com/file/d/...");
        }

        // Check if this file is already linked to this team
        var existing = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && r.GoogleId == fileId
                && r.ResourceType == GoogleResourceType.DriveFile
                && r.IsActive, ct);

        if (existing is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Drive file is already linked to this team.");
        }

        try
        {
            var drive = await GetDriveServiceAsync(ct);
            var request = drive.Files.Get(fileId);
            request.Fields = "id, name, webViewLink, mimeType, parents, driveId";
            request.SupportsAllDrives = true;
            var file = await request.ExecuteAsync(ct);

            if (string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            {
                return new LinkResourceResult(false,
                    ErrorMessage: "The provided URL points to a folder, not a file. Please use the 'Link Drive Resource' form instead.");
            }

            var filePath = await BuildFolderPathAsync(drive, file, ct);
            var now = _clock.GetCurrentInstant();

            // Check for an inactive record to reactivate
            var inactive = await _dbContext.GoogleResources
                .FirstOrDefaultAsync(r => r.TeamId == teamId
                    && r.GoogleId == fileId
                    && r.ResourceType == GoogleResourceType.DriveFile
                    && !r.IsActive, ct);

            GoogleResource resource;
            if (inactive is not null)
            {
                inactive.Name = filePath;
                inactive.Url = file.WebViewLink;
                inactive.LastSyncedAt = now;
                inactive.IsActive = true;
                inactive.ErrorMessage = null;
                resource = inactive;
            }
            else
            {
                resource = new GoogleResource
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    ResourceType = GoogleResourceType.DriveFile,
                    GoogleId = file.Id,
                    Name = filePath,
                    Url = file.WebViewLink,
                    ProvisionedAt = now,
                    LastSyncedAt = now,
                    IsActive = true,
                    DrivePermissionLevel = permissionLevel
                };
                _dbContext.GoogleResources.Add(resource);
            }

            if (inactive is not null)
            {
                inactive.DrivePermissionLevel = permissionLevel;
            }

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Linked Drive file {FileId} ({FileName}) to team {TeamId} with permission {Permission}",
                file.Id, file.Name, teamId, permissionLevel);

            return new LinkResourceResult(true, Resource: resource);
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google API error linking Drive file {FileId}: Code={Code} Message={Message}",
                fileId, ex.Error?.Code, ex.Error?.Message);
            var serviceAccountEmail = await GetServiceAccountEmailAsync(ct);
            var hint = ex.Error?.Code switch
            {
                404 => "The file was not found or the service account does not have access.",
                403 => "The service account does not have permission to access this file.",
                _ => $"Google API error ({ex.Error?.Code}): {ex.Error?.Message}"
            };
            return new LinkResourceResult(false,
                ErrorMessage: $"{hint} The file must be on a Shared Drive accessible to the service account.",
                ServiceAccountEmail: serviceAccountEmail);
        }
    }

    /// <inheritdoc />
    public async Task<LinkResourceResult> LinkDriveResourceAsync(Guid teamId, string url, DrivePermissionLevel permissionLevel = DrivePermissionLevel.Contributor, CancellationToken ct = default)
    {
        return await TeamResourceInputValidation.LinkDriveResourceAsync(
            teamId,
            url,
            permissionLevel,
            ct,
            LinkDriveFolderAsync,
            LinkDriveFileAsync);
    }

    /// <inheritdoc />
    public async Task<LinkResourceResult> LinkGroupAsync(Guid teamId, string groupEmail, CancellationToken ct = default)
    {
        var normalizedGroupEmail = TeamResourceInputValidation.NormalizeGroupEmail(groupEmail);
        if (normalizedGroupEmail is null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: TeamResourceValidationMessages.InvalidGroupEmail);
        }

        // Check if this group is already linked to this team
        var existing = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && EF.Functions.ILike(r.GoogleId, normalizedGroupEmail)
                && r.ResourceType == GoogleResourceType.Group
                && r.IsActive, ct);

        if (existing is not null)
        {
            return new LinkResourceResult(false,
                ErrorMessage: "This Google Group is already linked to this team.");
        }

        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync(ct);
            var lookupRequest = cloudIdentity.Groups.Lookup();
            lookupRequest.GroupKeyId = normalizedGroupEmail;
            var lookupResponse = await lookupRequest.ExecuteAsync(ct);

            var fullGroup = await cloudIdentity.Groups.Get(lookupResponse.Name).ExecuteAsync(ct);

            var now = _clock.GetCurrentInstant();
            var googleId = lookupResponse.Name["groups/".Length..];
            var emailLocal = normalizedGroupEmail.Split('@')[0];

            // Check for an inactive record to reactivate
            var inactive = await _dbContext.GoogleResources
                .FirstOrDefaultAsync(r => r.TeamId == teamId
                    && (r.GoogleId == googleId || EF.Functions.ILike(r.GoogleId, normalizedGroupEmail))
                    && r.ResourceType == GoogleResourceType.Group
                    && !r.IsActive, ct);

            GoogleResource resource;
            if (inactive is not null)
            {
                inactive.GoogleId = googleId;
                inactive.Name = normalizedGroupEmail;
                inactive.Url = $"https://groups.google.com/a/{_googleSettings.Domain}/g/{emailLocal}";
                inactive.LastSyncedAt = now;
                inactive.IsActive = true;
                inactive.ErrorMessage = null;
                resource = inactive;
            }
            else
            {
                resource = new GoogleResource
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    ResourceType = GoogleResourceType.Group,
                    GoogleId = googleId,
                    Name = normalizedGroupEmail,
                    Url = $"https://groups.google.com/a/{_googleSettings.Domain}/g/{emailLocal}",
                    ProvisionedAt = now,
                    LastSyncedAt = now,
                    IsActive = true
                };
                _dbContext.GoogleResources.Add(resource);
            }

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Linked Google Group {GroupEmail} ({GroupName}) to team {TeamId}",
                normalizedGroupEmail, fullGroup.DisplayName, teamId);

            return new LinkResourceResult(true, Resource: resource);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            _logger.LogWarning(ex, "Google Group not found when linking {GroupEmail} to team {TeamId}", normalizedGroupEmail, teamId);
            var serviceAccountEmail = await GetServiceAccountEmailAsync(ct);
            return new LinkResourceResult(false,
                ErrorMessage: "The Google Group was not found or the service account does not have access. " +
                    "Please add the service account as a Group Manager.",
                ServiceAccountEmail: serviceAccountEmail);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 403)
        {
            _logger.LogWarning(ex, "Permission denied when linking Google Group {GroupEmail} to team {TeamId}", normalizedGroupEmail, teamId);
            var serviceAccountEmail = await GetServiceAccountEmailAsync(ct);
            return new LinkResourceResult(false,
                ErrorMessage: "The service account does not have permission to access this group. " +
                    "Please add the service account as a Group Manager.",
                ServiceAccountEmail: serviceAccountEmail);
        }
    }

    /// <inheritdoc />
    public async Task UnlinkResourceAsync(Guid resourceId, CancellationToken ct = default)
    {
        var resource = await TeamResourcePersistence.DeactivateResourceAsync(_dbContext, resourceId, ct);
        if (resource is null)
        {
            return;
        }

        _logger.LogInformation("Unlinked resource {ResourceId} ({ResourceName})", resourceId, resource.Name);
    }

    /// <inheritdoc />
    public async Task<bool> CanManageTeamResourcesAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        return await TeamResourceAccessRules.CanManageTeamResourcesAsync(
            _teamService,
            _roleAssignmentService,
            _resourceSettings,
            teamId,
            userId,
            ct);
    }

    /// <inheritdoc />
    public async Task<string> GetServiceAccountEmailAsync(CancellationToken ct = default)
    {
        if (_serviceAccountEmail is not null)
        {
            return _serviceAccountEmail;
        }

        _serviceAccountEmail = await ExtractServiceAccountEmailAsync(ct);
        return _serviceAccountEmail;
    }

    private async Task<string> ExtractServiceAccountEmailAsync(CancellationToken ct)
    {
        string? json = null;

        if (!string.IsNullOrEmpty(_googleSettings.ServiceAccountKeyJson))
        {
            json = _googleSettings.ServiceAccountKeyJson;
        }
        else if (!string.IsNullOrEmpty(_googleSettings.ServiceAccountKeyPath))
        {
            json = await File.ReadAllTextAsync(_googleSettings.ServiceAccountKeyPath, ct);
        }

        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var emailElement))
            {
                return emailElement.GetString() ?? "unknown@serviceaccount.iam.gserviceaccount.com";
            }
        }

        return "unknown@serviceaccount.iam.gserviceaccount.com";
    }

    /// <summary>
    /// Builds a hierarchical path for a Drive folder by walking up the parent chain.
    /// Returns e.g. "SharedDrive / Parent / Child" or just "FolderName" if no parents are accessible.
    /// </summary>
    private async Task<string> BuildFolderPathAsync(
        DriveService drive, Google.Apis.Drive.v3.Data.File file, CancellationToken ct)
    {
        var segments = new List<string> { file.Name };
        var currentParents = file.Parents;
        var driveId = file.DriveId;

        // Walk up the parent chain (max 10 levels to avoid infinite loops)
        for (var i = 0; i < 10 && currentParents is { Count: > 0 }; i++)
        {
            var parentId = currentParents[0];

            // Stop if we've reached the Shared Drive root
            if (string.Equals(parentId, driveId, StringComparison.Ordinal))
            {
                // Try to get the Shared Drive name
                try
                {
                    var driveInfo = await drive.Drives.Get(driveId).ExecuteAsync(ct);
                    segments.Add(driveInfo.Name);
                }
                catch (Google.GoogleApiException ex)
                {
                    _logger.LogDebug(ex, "Service account cannot access Shared Drive metadata for {DriveId}", driveId);
                }
                break;
            }

            try
            {
                var parentRequest = drive.Files.Get(parentId);
                parentRequest.Fields = "id, name, parents, driveId";
                parentRequest.SupportsAllDrives = true;
                var parent = await parentRequest.ExecuteAsync(ct);

                segments.Add(parent.Name);
                currentParents = parent.Parents;
            }
            catch (Google.GoogleApiException ex)
            {
                _logger.LogDebug(ex, "Cannot access parent folder {ParentId} — stopping path walk", parentId);
                break;
            }
        }

        segments.Reverse();
        return string.Join(" / ", segments);
    }

    /// <summary>
    /// Parses a Google Drive folder ID from various URL formats.
    /// </summary>
    internal static string? ParseDriveFolderId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim();

        // Direct folder ID (no URL, just the ID itself)
        if (FolderIdPattern().IsMatch(input))
        {
            return input;
        }

        // https://drive.google.com/drive/folders/{id}
        // https://drive.google.com/drive/u/0/folders/{id}
        // https://drive.google.com/drive/folders/{id}?usp=sharing
        var match = DriveFolderUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        // https://drive.google.com/open?id={id}
        match = DriveOpenUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        return null;
    }

    private async Task<DriveService> GetDriveServiceAsync(CancellationToken ct)
    {
        if (_driveService is not null)
        {
            return _driveService;
        }

        var credential = await GetServiceAccountCredentialAsync(ct, DriveService.Scope.DriveReadonly);

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _driveService;
    }

    private async Task<CloudIdentityService> GetCloudIdentityServiceAsync(CancellationToken ct)
    {
        if (_cloudIdentityService is not null)
        {
            return _cloudIdentityService;
        }

        var credential = await GetServiceAccountCredentialAsync(ct,
            CloudIdentityService.Scope.CloudIdentityGroupsReadonly);

        _cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _cloudIdentityService;
    }

    /// <summary>
    /// Loads the service account credential WITHOUT impersonation.
    /// This authenticates as the service account itself to access pre-shared resources.
    /// </summary>
    private async Task<GoogleCredential> GetServiceAccountCredentialAsync(CancellationToken ct, params string[] scopes)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_googleSettings.ServiceAccountKeyJson))
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_googleSettings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, ct)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(_googleSettings.ServiceAccountKeyPath))
        {
            await using var stream = File.OpenRead(_googleSettings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, ct)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. Set ServiceAccountKeyPath or ServiceAccountKeyJson.");
        }

        // NO .CreateWithUser() — authenticate as the service account itself
        return credential.CreateScoped(scopes);
    }

    /// <summary>
    /// Parses a Google Drive file ID from various URL formats.
    /// Supports Google Docs, Sheets, Slides, and generic Drive file URLs.
    /// </summary>
    internal static string? ParseDriveFileId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim();

        // Direct file ID (no URL, just the ID itself)
        if (FileIdPattern().IsMatch(input))
        {
            return input;
        }

        // https://drive.google.com/file/d/{id}/...
        var match = DriveFileUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        // https://docs.google.com/spreadsheets/d/{id}/...
        // https://docs.google.com/document/d/{id}/...
        // https://docs.google.com/presentation/d/{id}/...
        // https://docs.google.com/forms/d/{id}/...
        match = GoogleDocsUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        // https://drive.google.com/open?id={id}
        match = DriveOpenUrlPattern().Match(input);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        return null;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{10,}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FolderIdPattern();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{10,}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FileIdPattern();

    [GeneratedRegex(@"drive\.google\.com/(?:drive/)?(?:u/\d+/)?folders/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveFolderUrlPattern();

    [GeneratedRegex(@"drive\.google\.com/file/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveFileUrlPattern();

    [GeneratedRegex(@"docs\.google\.com/(?:spreadsheets|document|presentation|forms)/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GoogleDocsUrlPattern();

    [GeneratedRegex(@"drive\.google\.com/open\?id=(?<id>[a-zA-Z0-9_-]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveOpenUrlPattern();

    public async Task<GoogleResource?> GetResourceByIdAsync(Guid resourceId, CancellationToken ct = default)
    {
        return await TeamResourcePersistence.GetResourceByIdAsync(_dbContext, resourceId, ct);
    }

    /// <inheritdoc />
    public async Task UpdatePermissionLevelAsync(Guid resourceId, DrivePermissionLevel level, CancellationToken ct = default)
    {
        var resource = await _dbContext.GoogleResources.FindAsync([resourceId], ct);
        if (resource is null) return;

        resource.DrivePermissionLevel = level;
        await _dbContext.SaveChangesAsync(ct);
        _logger.LogInformation("Updated DrivePermissionLevel to {Level} for resource {ResourceId}", level, resourceId);
    }
}
