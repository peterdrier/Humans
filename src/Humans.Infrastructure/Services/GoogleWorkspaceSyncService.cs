using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
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
/// Google Workspace API implementation for Drive and Groups management.
/// </summary>
public class GoogleWorkspaceSyncService : IGoogleSyncService
{
    private readonly HumansDbContext _dbContext;
    private readonly GoogleWorkspaceSettings _settings;
    private readonly IClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<GoogleWorkspaceSyncService> _logger;

    private DirectoryService? _directoryService;
    private DriveService? _driveService;

    public GoogleWorkspaceSyncService(
        HumansDbContext dbContext,
        IOptions<GoogleWorkspaceSettings> settings,
        IClock clock,
        IAuditLogService auditLogService,
        ILogger<GoogleWorkspaceSyncService> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _clock = clock;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService != null)
        {
            return _directoryService;
        }

        var credential = await GetCredentialAsync(
            DirectoryService.Scope.AdminDirectoryGroup,
            DirectoryService.Scope.AdminDirectoryGroupMember);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
    }

    private async Task<DriveService> GetDriveServiceAsync()
    {
        if (_driveService != null)
        {
            return _driveService;
        }

        var credential = await GetCredentialAsync(DriveService.Scope.Drive);

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _driveService;
    }

    private async Task<GoogleCredential> GetCredentialAsync(params string[] scopes)
    {
        GoogleCredential credential;

        if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyJson))
        {
            // Use CredentialFactory for secure credential loading
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_settings.ServiceAccountKeyJson));
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else if (!string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
        {
            await using var stream = File.OpenRead(_settings.ServiceAccountKeyPath);
            credential = (await CredentialFactory.FromStreamAsync<ServiceAccountCredential>(stream, CancellationToken.None)
                .ConfigureAwait(false)).ToGoogleCredential();
        }
        else
        {
            throw new InvalidOperationException(
                "Google Workspace credentials not configured. Set ServiceAccountKeyPath or ServiceAccountKeyJson.");
        }

        return credential.CreateScoped(scopes);
    }

    /// <inheritdoc />
    public async Task<GoogleResource> ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        // Check for existing active folder — return it if found (idempotent)
        var existing = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.TeamId == teamId
                && r.ResourceType == GoogleResourceType.DriveFolder
                && r.IsActive, cancellationToken);

        if (existing != null)
        {
            _logger.LogInformation("Team {TeamId} already has active Drive folder {FolderId}", teamId, existing.GoogleId);
            return existing;
        }

        _logger.LogInformation("Provisioning Drive folder '{FolderName}' for team {TeamId}", folderName, teamId);

        var drive = await GetDriveServiceAsync();
        var now = _clock.GetCurrentInstant();

        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = folderName,
            MimeType = "application/vnd.google-apps.folder"
        };

        if (!string.IsNullOrEmpty(_settings.TeamFoldersParentId))
        {
            fileMetadata.Parents = [_settings.TeamFoldersParentId];
        }

        var request = drive.Files.Create(fileMetadata);
        request.Fields = "id, name, webViewLink";
        request.SupportsAllDrives = true;
        var folder = await request.ExecuteAsync(cancellationToken);

        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFolder,
            GoogleId = folder.Id,
            Name = folder.Name,
            Url = folder.WebViewLink,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        _dbContext.GoogleResources.Add(resource);

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, "GoogleResource", resource.Id,
            $"Provisioned Drive folder '{folder.Name}' for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: "Team");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created Drive folder {FolderId} for team {TeamId}", folder.Id, teamId);
        return resource;
    }

    /// <inheritdoc />
    public async Task<GoogleResource> ProvisionTeamGroupAsync(
        Guid teamId,
        string groupEmail,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Provisioning Google Group '{GroupEmail}' for team {TeamId}", groupEmail, teamId);

        var directory = await GetDirectoryServiceAsync();
        var now = _clock.GetCurrentInstant();

        var group = new Group
        {
            Email = groupEmail,
            Name = groupName,
            Description = $"Mailing list for {groupName} team"
        };

        var createdGroup = await directory.Groups.Insert(group).ExecuteAsync(cancellationToken);

        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = createdGroup.Id,
            Name = groupName,
            Url = $"https://groups.google.com/a/{_settings.Domain}/g/{groupEmail.Split('@')[0]}",
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        _dbContext.GoogleResources.Add(resource);

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, "GoogleResource", resource.Id,
            $"Provisioned Google Group '{groupName}' ({groupEmail}) for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: "Team");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created Google Group {GroupId} ({GroupEmail}) for team {TeamId}",
            createdGroup.Id, groupEmail, teamId);

        return resource;
    }

    /// <inheritdoc />
    public async Task AddUserToGroupAsync(
        Guid groupResourceId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var resource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.Id == groupResourceId, cancellationToken);

        if (resource == null || resource.ResourceType != GoogleResourceType.Group)
        {
            _logger.LogWarning("Group resource {ResourceId} not found", groupResourceId);
            return;
        }

        _logger.LogInformation("Adding {UserEmail} to group {GroupId}", userEmail, resource.GoogleId);

        var directory = await GetDirectoryServiceAsync();

        var member = new Member
        {
            Email = userEmail,
            Role = "MEMBER"
        };

        try
        {
            await directory.Members.Insert(member, resource.GoogleId).ExecuteAsync(cancellationToken);

            await _auditLogService.LogGoogleSyncAsync(
                AuditAction.GoogleResourceAccessGranted, groupResourceId,
                $"Granted Google Group access to {userEmail} ({resource.Name})",
                nameof(GoogleWorkspaceSyncService),
                userEmail, "MEMBER", GoogleSyncSource.ManualSync, success: true);

            _logger.LogInformation("Added {UserEmail} to group {GroupId}", userEmail, resource.GoogleId);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 409)
        {
            _logger.LogDebug("User {UserEmail} is already a member of group {GroupId}", userEmail, resource.GoogleId);
        }
    }

    /// <inheritdoc />
    public Task RemoveUserFromGroupAsync(
        Guid groupResourceId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        // Removal disabled — sync is add-only until automated sync is validated
        _logger.LogInformation("Skipping Google Group removal for {UserEmail} from {GroupResourceId} (removal disabled)",
            userEmail, groupResourceId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SyncTeamGroupMembersAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Syncing group members for team {TeamId}", teamId);

        var groupResource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.ResourceType == GoogleResourceType.Group && r.IsActive,
                cancellationToken);

        if (groupResource == null)
        {
            _logger.LogWarning("No active Google Group found for team {TeamId}", teamId);
            return;
        }

        // Get current team members
        var teamMembers = await _dbContext.TeamMembers
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .Select(tm => tm.User.Email)
            .Where(email => email != null)
            .ToListAsync(cancellationToken);

        // Get current group members from Google
        var directory = await GetDirectoryServiceAsync();
        var currentGroupMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? pageToken = null;
            do
            {
                var membersRequest = directory.Members.List(groupResource.GoogleId);
                membersRequest.MaxResults = 200;
                if (pageToken != null)
                {
                    membersRequest.PageToken = pageToken;
                }

                var membersResponse = await membersRequest.ExecuteAsync(cancellationToken);

                if (membersResponse.MembersValue != null)
                {
                    foreach (var member in membersResponse.MembersValue)
                    {
                        if (!string.IsNullOrEmpty(member.Email))
                        {
                            currentGroupMembers.Add(member.Email);
                        }
                    }
                }

                pageToken = membersResponse.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            _logger.LogWarning("Group {GroupId} not found in Google", groupResource.GoogleId);
            return;
        }

        // Add missing members
        foreach (var email in teamMembers)
        {
            if (!currentGroupMembers.Contains(email!))
            {
                await AddUserToGroupAsync(groupResource.Id, email!, cancellationToken);
            }
        }

        // Removal disabled — sync is add-only until automated sync is validated

        groupResource.LastSyncedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Synced group members for team {TeamId}: {MemberCount} members", teamId, teamMembers.Count);
    }

    /// <inheritdoc />
    public async Task SyncResourcePermissionsAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        var resource = await _dbContext.GoogleResources
            .Include(r => r.Team)
            .ThenInclude(t => t.Members)
            .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);

        if (resource == null)
        {
            _logger.LogWarning("Resource {ResourceId} not found", resourceId);
            return;
        }

        if (resource.ResourceType == GoogleResourceType.Group)
        {
            await SyncTeamGroupMembersAsync(resource.TeamId, cancellationToken);
            return;
        }

        var drive = await GetDriveServiceAsync();

        var expectedEmails = resource.Team.Members
            .Where(m => m.LeftAt == null && !string.IsNullOrEmpty(m.User.Email))
            .Select(m => m.User.Email!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Get current Drive permissions
        var currentPermissions = await ListDrivePermissionsAsync(drive, resource.GoogleId, cancellationToken);

        // Check ALL user permissions (including inherited from Shared Drive).
        // Users with inherited access don't need a direct permission added.
        var allUserEmails = currentPermissions
            .Where(IsAnyUserPermission)
            .Select(p => p.EmailAddress!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Add missing permissions (skip users who already have access via inheritance or direct)
        foreach (var email in expectedEmails)
        {
            if (!allUserEmails.Contains(email))
            {
                try
                {
                    var permission = new Google.Apis.Drive.v3.Data.Permission
                    {
                        Type = "user",
                        Role = "writer",
                        EmailAddress = email
                    };

                    var createReq = drive.Permissions.Create(permission, resource.GoogleId);
                    createReq.SupportsAllDrives = true;
                    await createReq.ExecuteAsync(cancellationToken);

                    await _auditLogService.LogGoogleSyncAsync(
                        AuditAction.GoogleResourceAccessGranted, resourceId,
                        $"Granted Drive folder access to {email} ({resource.Name}) during sync",
                        nameof(GoogleWorkspaceSyncService),
                        email, "writer", GoogleSyncSource.ManualSync, success: true);
                }
                catch (Google.GoogleApiException ex) when (ex.Error?.Code == 400)
                {
                    _logger.LogDebug("Permission already exists for {Email} on {ResourceId}", email, resourceId);
                }
            }
        }

        // Removal disabled — sync is add-only until automated sync is validated

        resource.LastSyncedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SyncAllResourcesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting sync of all Google resources");

        var resources = await _dbContext.GoogleResources
            .Where(r => r.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var resource in resources)
        {
            try
            {
                await SyncResourcePermissionsAsync(resource.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing resource {ResourceId}", resource.Id);
                resource.ErrorMessage = ex.Message;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Completed sync of {Count} Google resources", resources.Count);
    }

    /// <inheritdoc />
    public async Task<GoogleResource?> GetResourceStatusAsync(
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.GoogleResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], cancellationToken);
        if (user?.Email == null)
        {
            _logger.LogWarning("User {UserId} not found or has no email", userId);
            return;
        }

        var resources = await _dbContext.GoogleResources
            .Where(r => r.TeamId == teamId && r.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var resource in resources)
        {
            if (resource.ResourceType == GoogleResourceType.Group)
            {
                await AddUserToGroupAsync(resource.Id, user.Email, cancellationToken);
            }
            else
            {
                // Add Drive permission
                var drive = await GetDriveServiceAsync();
                var permission = new Google.Apis.Drive.v3.Data.Permission
                {
                    Type = "user",
                    Role = "writer",
                    EmailAddress = user.Email
                };

                try
                {
                    var createReq = drive.Permissions.Create(permission, resource.GoogleId);
                    createReq.SupportsAllDrives = true;
                    await createReq.ExecuteAsync(cancellationToken);

                    await _auditLogService.LogGoogleSyncAsync(
                        AuditAction.GoogleResourceAccessGranted, resource.Id,
                        $"Granted Drive folder access to {user.Email} ({resource.Name})",
                        nameof(GoogleWorkspaceSyncService),
                        user.Email, "writer", GoogleSyncSource.TeamMemberJoined, success: true,
                        relatedEntityId: userId, relatedEntityType: "User");
                }
                catch (Google.GoogleApiException ex) when (ex.Error?.Code == 400)
                {
                    _logger.LogDebug("Permission already exists for {Email}", user.Email);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveUserFromTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Removal disabled — sync is add-only until automated sync is validated
        _logger.LogInformation("Skipping Google resource removal for user {UserId} from team {TeamId} (removal disabled)",
            userId, teamId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SyncPreviewResult> PreviewSyncAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting sync preview of all Google resources");

        var resources = await _dbContext.GoogleResources
            .Include(r => r.Team)
                .ThenInclude(t => t.Members.Where(m => m.LeftAt == null))
                    .ThenInclude(m => m.User)
            .Where(r => r.IsActive)
            .ToListAsync(cancellationToken);

        var diffs = new List<ResourceSyncDiff>();

        foreach (var resource in resources)
        {
            try
            {
                var diff = resource.ResourceType == GoogleResourceType.Group
                    ? await PreviewGroupSyncAsync(resource, cancellationToken)
                    : await PreviewDriveFolderSyncAsync(resource, cancellationToken);

                diffs.Add(diff);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error previewing resource {ResourceId}", resource.Id);
                diffs.Add(new ResourceSyncDiff
                {
                    ResourceId = resource.Id,
                    ResourceName = resource.Name,
                    ResourceType = resource.ResourceType.ToString(),
                    TeamName = resource.Team.Name,
                    GoogleId = resource.GoogleId,
                    Url = resource.Url,
                    ErrorMessage = ex.Message
                });
            }
        }

        _logger.LogInformation("Completed sync preview: {Total} resources, {Drift} drifted",
            diffs.Count, diffs.Count(d => !d.IsInSync));

        return new SyncPreviewResult { Diffs = diffs };
    }

    private async Task<ResourceSyncDiff> PreviewGroupSyncAsync(
        GoogleResource resource, CancellationToken cancellationToken)
    {
        var expectedEmails = resource.Team.Members
            .Where(m => m.LeftAt == null && !string.IsNullOrEmpty(m.User.Email))
            .Select(m => m.User.Email!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Get actual members from Google
        var directory = await GetDirectoryServiceAsync();
        var actualEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? pageToken = null;
        do
        {
            var membersRequest = directory.Members.List(resource.GoogleId);
            membersRequest.MaxResults = 200;
            if (pageToken != null)
            {
                membersRequest.PageToken = pageToken;
            }

            var membersResponse = await membersRequest.ExecuteAsync(cancellationToken);

            if (membersResponse.MembersValue != null)
            {
                foreach (var member in membersResponse.MembersValue)
                {
                    if (!string.IsNullOrEmpty(member.Email))
                    {
                        actualEmails.Add(member.Email);
                    }
                }
            }

            pageToken = membersResponse.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return new ResourceSyncDiff
        {
            ResourceId = resource.Id,
            ResourceName = resource.Name,
            ResourceType = resource.ResourceType.ToString(),
            TeamName = resource.Team.Name,
            GoogleId = resource.GoogleId,
            Url = resource.Url,
            MembersToAdd = expectedEmails.Except(actualEmails, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            // Removal disabled — sync is add-only until automated sync is validated
            MembersToRemove = []
        };
    }

    private async Task<ResourceSyncDiff> PreviewDriveFolderSyncAsync(
        GoogleResource resource, CancellationToken cancellationToken)
    {
        var expectedEmails = resource.Team.Members
            .Where(m => m.LeftAt == null && !string.IsNullOrEmpty(m.User.Email))
            .Select(m => m.User.Email!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var drive = await GetDriveServiceAsync();
        var permissions = await ListDrivePermissionsAsync(drive, resource.GoogleId, cancellationToken);

        // For "members to add", check ALL user permissions (including inherited from Shared Drive).
        // Users with inherited access don't need a direct permission added.
        var allUserEmails = permissions
            .Where(IsAnyUserPermission)
            .Select(p => p.EmailAddress!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ResourceSyncDiff
        {
            ResourceId = resource.Id,
            ResourceName = resource.Name,
            ResourceType = resource.ResourceType.ToString(),
            TeamName = resource.Team.Name,
            GoogleId = resource.GoogleId,
            Url = resource.Url,
            MembersToAdd = expectedEmails.Except(allUserEmails, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            // Removal disabled — sync is add-only until automated sync is validated
            MembersToRemove = []
        };
    }

    /// <summary>
    /// Returns true if this is any user permission (direct or inherited), excluding
    /// service accounts and non-user types. Used to determine if a user already has
    /// access in any form and doesn't need to be added.
    /// </summary>
    private static bool IsAnyUserPermission(Google.Apis.Drive.v3.Data.Permission perm)
    {
        if (!string.Equals(perm.Type, "user", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(perm.EmailAddress))
            return false;
        if (perm.EmailAddress.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// Returns true if this is a direct (non-inherited) user permission that we manage.
    /// Excludes inherited permissions (from Shared Drive), owner role, service accounts,
    /// and non-user permission types (domain, group, anyone).
    /// </summary>
    private static bool IsDirectManagedPermission(Google.Apis.Drive.v3.Data.Permission perm)
    {
        if (!string.Equals(perm.Type, "user", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(perm.Role, "owner", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(perm.EmailAddress))
            return false;
        if (perm.EmailAddress.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase))
            return false;

        // On Shared Drives, permissionDetails contains inheritance info.
        // A permission is inherited if ALL its detail entries are inherited.
        if (perm.PermissionDetails != null && perm.PermissionDetails.Count > 0)
        {
            if (perm.PermissionDetails.All(d => d.Inherited == true))
                return false;
        }

        return true;
    }

    private static async Task<List<Google.Apis.Drive.v3.Data.Permission>> ListDrivePermissionsAsync(
        DriveService drive, string fileId, CancellationToken cancellationToken)
    {
        var permissions = new List<Google.Apis.Drive.v3.Data.Permission>();
        string? pageToken = null;

        do
        {
            var listReq = drive.Permissions.List(fileId);
            listReq.SupportsAllDrives = true;
            listReq.Fields = "nextPageToken, permissions(id, emailAddress, role, type, permissionDetails)";
            if (pageToken != null)
            {
                listReq.PageToken = pageToken;
            }

            var response = await listReq.ExecuteAsync(cancellationToken);
            if (response.Permissions != null)
            {
                permissions.AddRange(response.Permissions);
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return permissions;
    }

    /// <inheritdoc />
    public async Task RestoreUserToAllTeamsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restoring Google resource access for user {UserId}", userId);

        var user = await _dbContext.Users
            .Include(u => u.TeamMemberships.Where(tm => tm.LeftAt == null))
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found for access restoration", userId);
            return;
        }

        foreach (var membership in user.TeamMemberships)
        {
            try
            {
                await AddUserToTeamResourcesAsync(membership.TeamId, userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring access for user {UserId} to team {TeamId}",
                    userId, membership.TeamId);
            }
        }
    }
}
