using System.Text.Json;
using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.CloudIdentity.v1;
using Google.Apis.CloudIdentity.v1.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Groupssettings.v1;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
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
    private readonly ISyncSettingsService _syncSettingsService;
    private readonly ILogger<GoogleWorkspaceSyncService> _logger;

    private CloudIdentityService? _cloudIdentityService;
    private DirectoryService? _directoryService;
    private DriveService? _driveService;
    private GroupssettingsService? _groupssettingsService;
    private string? _serviceAccountEmail;

    public GoogleWorkspaceSyncService(
        HumansDbContext dbContext,
        IOptions<GoogleWorkspaceSettings> settings,
        IClock clock,
        IAuditLogService auditLogService,
        ISyncSettingsService syncSettingsService,
        ILogger<GoogleWorkspaceSyncService> logger)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _clock = clock;
        _auditLogService = auditLogService;
        _syncSettingsService = syncSettingsService;
        _logger = logger;
    }

    private async Task<CloudIdentityService> GetCloudIdentityServiceAsync()
    {
        if (_cloudIdentityService is not null)
        {
            return _cloudIdentityService;
        }

        var credential = await GetCredentialAsync(
            CloudIdentityService.Scope.CloudIdentityGroups);

        _cloudIdentityService = new CloudIdentityService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _cloudIdentityService;
    }

    private async Task<DriveService> GetDriveServiceAsync()
    {
        if (_driveService is not null)
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

    private async Task<GroupssettingsService> GetGroupssettingsServiceAsync()
    {
        if (_groupssettingsService is not null)
        {
            return _groupssettingsService;
        }

        var credential = await GetCredentialAsync(GroupssettingsService.Scope.AppsGroupsSettings);

        _groupssettingsService = new GroupssettingsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _groupssettingsService;
    }

    private async Task<DirectoryService> GetDirectoryServiceAsync()
    {
        if (_directoryService is not null)
        {
            return _directoryService;
        }

        var credential = await GetCredentialAsync(
            DirectoryService.Scope.AdminDirectoryUserReadonly,
            DirectoryService.Scope.AdminDirectoryGroupReadonly);

        _directoryService = new DirectoryService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Humans"
        });

        return _directoryService;
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

    private async Task<string> GetServiceAccountEmailAsync()
    {
        if (_serviceAccountEmail is not null)
            return _serviceAccountEmail;

        string? json = _settings.ServiceAccountKeyJson;
        if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(_settings.ServiceAccountKeyPath))
            json = await File.ReadAllTextAsync(_settings.ServiceAccountKeyPath);

        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("client_email", out var emailElement))
                _serviceAccountEmail = emailElement.GetString();
        }

        _serviceAccountEmail ??= "unknown@serviceaccount.iam.gserviceaccount.com";
        return _serviceAccountEmail;
    }

    /// <summary>
    /// Gets active members of all child teams for a department (subteam member rollup).
    /// Returns empty if the team has no children.
    /// </summary>
    private async Task<List<TeamMember>> GetChildTeamMembersAsync(
        Guid parentTeamId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.User)
            .Include(tm => tm.Team)
            .Where(tm =>
                tm.Team.ParentTeamId == parentTeamId &&
                tm.Team.IsActive &&
                tm.LeftAt == null)
            .ToListAsync(cancellationToken);
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

        if (existing is not null)
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
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Provisioned Drive folder '{folder.Name}' for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

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

        var cloudIdentity = await GetCloudIdentityServiceAsync();
        var now = _clock.GetCurrentInstant();

        var group = new Google.Apis.CloudIdentity.v1.Data.Group
        {
            GroupKey = new EntityKey { Id = groupEmail },
            DisplayName = groupName,
            Description = $"Mailing list for {groupName} team",
            Parent = $"customers/{_settings.CustomerId}",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cloudidentity.googleapis.com/groups.discussion_forum"] = ""
            }
        };

        var createRequest = cloudIdentity.Groups.Create(group);
        createRequest.InitialGroupConfig =
            Google.Apis.CloudIdentity.v1.GroupsResource.CreateRequest.InitialGroupConfigEnum.WITHINITIALOWNER;
        var operation = await createRequest.ExecuteAsync(cancellationToken);

        // Extract group ID from the operation response
        var groupResourceName = (string)operation.Response["name"];
        var createdGroupId = groupResourceName["groups/".Length..];

        // Apply configured group settings
        try
        {
            var groupssettingsService = await GetGroupssettingsServiceAsync();
            var groupSettings = BuildExpectedGroupSettings();
            await groupssettingsService.Groups.Update(groupSettings, groupEmail).ExecuteAsync(cancellationToken);
            _logger.LogInformation("Applied group settings to '{GroupEmail}'", groupEmail);
        }
        catch (Exception ex)
        {
            // Non-fatal: group was created, settings can be applied later
            _logger.LogWarning(ex, "Failed to apply group settings to '{GroupEmail}'. Group was created with Google defaults", groupEmail);
        }

        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = createdGroupId,
            Name = groupName,
            Url = $"https://groups.google.com/a/{_settings.Domain}/g/{groupEmail.Split('@')[0]}",
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        _dbContext.GoogleResources.Add(resource);

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Provisioned Google Group '{groupName}' ({groupEmail}) for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created Google Group {GroupId} ({GroupEmail}) for team {TeamId}",
            createdGroupId, groupEmail, teamId);

        return resource;
    }

    /// <inheritdoc />
    /// <remarks>
    /// GATEWAY METHOD: This is the ONLY way to add a user to a Google Group.
    /// All code paths (outbox, reconciliation, manual sync) must call this method.
    /// Respects SyncSettings — skips if GoogleGroups mode is None.
    /// </remarks>
    public async Task AddUserToGroupAsync(
        Guid groupResourceId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
        if (mode == SyncMode.None)
        {
            _logger.LogDebug("Skipping AddUserToGroup — GoogleGroups sync mode is None");
            return;
        }

        var resource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.Id == groupResourceId, cancellationToken);

        if (resource is null || resource.ResourceType != GoogleResourceType.Group)
        {
            _logger.LogWarning("Group resource {ResourceId} not found", groupResourceId);
            return;
        }

        var cloudIdentity = await GetCloudIdentityServiceAsync();

        var membership = new Membership
        {
            PreferredMemberKey = new EntityKey { Id = userEmail },
            Roles = [new MembershipRole { Name = "MEMBER" }]
        };

        try
        {
            await cloudIdentity.Groups.Memberships
                .Create(membership, $"groups/{resource.GoogleId}")
                .ExecuteAsync(cancellationToken);

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
    /// <remarks>
    /// GATEWAY METHOD: This is the ONLY way to remove a user from a Google Group.
    /// All code paths (reconciliation, manual sync) must call this method.
    /// Respects SyncSettings — skips if GoogleGroups mode is not AddAndRemove.
    /// </remarks>
    public async Task RemoveUserFromGroupAsync(
        Guid groupResourceId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
        if (mode != SyncMode.AddAndRemove)
        {
            _logger.LogDebug("Skipping RemoveUserFromGroup — GoogleGroups sync mode is {Mode}", mode);
            return;
        }

        var resource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.Id == groupResourceId, cancellationToken);

        if (resource is null || resource.ResourceType != GoogleResourceType.Group)
        {
            _logger.LogWarning("Group resource {ResourceId} not found", groupResourceId);
            return;
        }

        var cloudIdentity = await GetCloudIdentityServiceAsync();

        // Look up membership name for this email
        string? membershipName = null;
        string? nextPageToken = null;
        do
        {
            var membersRequest = cloudIdentity.Groups.Memberships.List($"groups/{resource.GoogleId}");
            membersRequest.PageSize = 200;
            membersRequest.PageToken = nextPageToken;
            var membersResponse = await membersRequest.ExecuteAsync(cancellationToken);

            var match = membersResponse.Memberships?.FirstOrDefault(m =>
                string.Equals(m.PreferredMemberKey?.Id, userEmail, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                membershipName = match.Name;
                break;
            }
            nextPageToken = membersResponse.NextPageToken;
        } while (nextPageToken is not null);

        if (membershipName is null)
        {
            _logger.LogDebug("User {UserEmail} not found in group {GroupId}", userEmail, resource.GoogleId);
            return;
        }

        await cloudIdentity.Groups.Memberships.Delete(membershipName)
            .ExecuteAsync(cancellationToken);

        await _auditLogService.LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessRevoked, groupResourceId,
            $"Removed {userEmail} from Google Group ({resource.Name})",
            nameof(GoogleWorkspaceSyncService),
            userEmail, "MEMBER", GoogleSyncSource.ManualSync, success: true);

        _logger.LogInformation("Removed {UserEmail} from group {GroupId}", userEmail, resource.GoogleId);
    }

    /// <summary>
    /// GATEWAY METHOD: This is the ONLY way to add a user to a Google Drive resource.
    /// All code paths (outbox, reconciliation, manual sync) must call this method.
    /// Respects SyncSettings — skips if GoogleDrive mode is None.
    /// </summary>
    private async Task AddUserToDriveAsync(
        GoogleResource resource,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            _logger.LogDebug("Skipping AddUserToDrive — GoogleDrive sync mode is None");
            return;
        }

        var drive = await GetDriveServiceAsync();
        var apiRole = resource.DrivePermissionLevel.ToApiRole();
        var permission = new Google.Apis.Drive.v3.Data.Permission
        {
            Type = "user",
            Role = apiRole,
            EmailAddress = userEmail
        };

        try
        {
            var createReq = drive.Permissions.Create(permission, resource.GoogleId);
            createReq.SupportsAllDrives = true;
            await createReq.ExecuteAsync(cancellationToken);

            await _auditLogService.LogGoogleSyncAsync(
                AuditAction.GoogleResourceAccessGranted, resource.Id,
                $"Granted Drive access ({resource.DrivePermissionLevel}) to {userEmail} ({resource.Name})",
                nameof(GoogleWorkspaceSyncService),
                userEmail, apiRole, GoogleSyncSource.ManualSync, success: true);

            _logger.LogInformation("Granted Drive access to {Email} on {GoogleId}", userEmail, resource.GoogleId);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 400)
        {
            _logger.LogDebug("Permission already exists for {Email} on {GoogleId}", userEmail, resource.GoogleId);
        }
    }

    /// <summary>
    /// GATEWAY METHOD: This is the ONLY way to remove a user from a Google Drive resource.
    /// All code paths (reconciliation, manual sync) must call this method.
    /// Respects SyncSettings — skips if GoogleDrive mode is not AddAndRemove.
    /// </summary>
    private async Task RemoveUserFromDriveAsync(
        GoogleResource resource,
        string permissionId,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode != SyncMode.AddAndRemove)
        {
            _logger.LogDebug("Skipping RemoveUserFromDrive — GoogleDrive sync mode is {Mode}", mode);
            return;
        }

        var drive = await GetDriveServiceAsync();
        var deleteReq = drive.Permissions.Delete(resource.GoogleId, permissionId);
        deleteReq.SupportsAllDrives = true;
        await deleteReq.ExecuteAsync(cancellationToken);

        await _auditLogService.LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessRevoked, resource.Id,
            $"Removed Drive access for {userEmail} ({resource.Name})",
            nameof(GoogleWorkspaceSyncService),
            userEmail, resource.DrivePermissionLevel.ToApiRole(), GoogleSyncSource.ManualSync, success: true);

        _logger.LogInformation("Removed Drive access for {Email} on {GoogleId}", userEmail, resource.GoogleId);
    }

    /// <inheritdoc />
    public async Task SyncTeamGroupMembersAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Syncing group members for team {TeamId}", teamId);

        var groupResource = await _dbContext.GoogleResources
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.ResourceType == GoogleResourceType.Group && r.IsActive,
                cancellationToken);

        if (groupResource is null)
        {
            _logger.LogWarning("No active Google Group found for team {TeamId}", teamId);
            return;
        }

        // Get current team members
        var teamMembers = await _dbContext.TeamMembers
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .Select(tm => tm.User.GoogleEmail ?? tm.User.Email)
            .Where(email => email != null)
            .ToListAsync(cancellationToken);

        // Get current group members from Google via Cloud Identity
        var cloudIdentity = await GetCloudIdentityServiceAsync();
        var currentGroupMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? pageToken = null;
            do
            {
                var membersRequest = cloudIdentity.Groups.Memberships.List($"groups/{groupResource.GoogleId}");
                membersRequest.PageSize = 200;
                if (pageToken is not null)
                {
                    membersRequest.PageToken = pageToken;
                }

                var membersResponse = await membersRequest.ExecuteAsync(cancellationToken);

                if (membersResponse.Memberships is not null)
                {
                    foreach (var membership in membersResponse.Memberships)
                    {
                        var email = membership.PreferredMemberKey?.Id;
                        if (!string.IsNullOrEmpty(email))
                        {
                            currentGroupMembers.Add(email);
                        }
                    }
                }

                pageToken = membersResponse.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code is 404 or 403)
        {
            _logger.LogWarning("Group {GroupId} not found in Google (HTTP {Code})", groupResource.GoogleId, ex.Error.Code);
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
        var googleEmail = user?.GetGoogleServiceEmail();
        if (googleEmail is null)
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
                await AddUserToGroupAsync(resource.Id, googleEmail, cancellationToken);
            }
            else
            {
                await AddUserToDriveAsync(resource, googleEmail, cancellationToken);
            }
        }

        // Subteam member rollup: also add to parent department resources
        var team = await _dbContext.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team?.ParentTeamId is not null)
        {
            var parentResources = await _dbContext.GoogleResources
                .Where(r => r.TeamId == team.ParentTeamId && r.IsActive)
                .ToListAsync(cancellationToken);

            foreach (var resource in parentResources)
            {
                if (resource.ResourceType == GoogleResourceType.Group)
                {
                    await AddUserToGroupAsync(resource.Id, googleEmail, cancellationToken);
                }
                else
                {
                    await AddUserToDriveAsync(resource, googleEmail, cancellationToken);
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
        // Individual user removal is a no-op — removals are handled by the
        // reconciliation job via RemoveUserFromGroupAsync/RemoveUserFromDriveAsync.
        _logger.LogDebug("Per-user removal deferred to reconciliation for user {UserId} team {TeamId}",
            userId, teamId);
        return Task.CompletedTask;
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
        if (perm.PermissionDetails is not null && perm.PermissionDetails.Count > 0)
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
            if (pageToken is not null)
            {
                listReq.PageToken = pageToken;
            }

            var response = await listReq.ExecuteAsync(cancellationToken);
            if (response.Permissions is not null)
            {
                permissions.AddRange(response.Permissions);
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return permissions;
    }

    /// <inheritdoc />
    public async Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SyncResourcesByType: type={ResourceType}, action={Action}", resourceType, action);

        var resources = await _dbContext.GoogleResources
            .Include(r => r.Team)
                .ThenInclude(t => t.Members.Where(tm => tm.LeftAt == null))
                    .ThenInclude(tm => tm.User)
            .Where(r => r.ResourceType == resourceType && r.IsActive)
            .ToListAsync(cancellationToken);

        var diffs = new List<ResourceSyncDiff>();
        var now = _clock.GetCurrentInstant();

        if (resourceType == GoogleResourceType.Group)
        {
            // Groups: one group per team
            foreach (var resource in resources)
            {
                var diff = await SyncGroupResourceAsync(resource, action, now, cancellationToken);
                diffs.Add(diff);
            }
        }
        else
        {
            // Drive resources: group by GoogleId since multiple teams can share one resource
            var grouped = resources.GroupBy(r => r.GoogleId, StringComparer.Ordinal);
            foreach (var group in grouped)
            {
                var diff = await SyncDriveResourceGroupAsync(group.ToList(), action, now, cancellationToken);
                diffs.Add(diff);
            }
        }

        if (action == SyncAction.Execute)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return new SyncPreviewResult { Diffs = diffs };
    }

    /// <inheritdoc />
    public async Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SyncSingleResource: resourceId={ResourceId}, action={Action}", resourceId, action);

        var resource = await _dbContext.GoogleResources
            .Include(r => r.Team)
                .ThenInclude(t => t.Members.Where(tm => tm.LeftAt == null))
                    .ThenInclude(tm => tm.User)
            .FirstOrDefaultAsync(r => r.Id == resourceId, cancellationToken);

        if (resource is null)
        {
            return new ResourceSyncDiff
            {
                ResourceId = resourceId,
                ErrorMessage = "Resource not found"
            };
        }

        var now = _clock.GetCurrentInstant();
        ResourceSyncDiff diff;

        if (resource.ResourceType == GoogleResourceType.Group)
        {
            diff = await SyncGroupResourceAsync(resource, action, now, cancellationToken);
        }
        else
        {
            // Drive resource: find ALL resources with same GoogleId to get full team union
            var allWithSameGoogleId = await _dbContext.GoogleResources
                .Include(r => r.Team)
                    .ThenInclude(t => t.Members.Where(tm => tm.LeftAt == null))
                        .ThenInclude(tm => tm.User)
                .Where(r => r.GoogleId == resource.GoogleId && r.IsActive)
                .ToListAsync(cancellationToken);

            diff = await SyncDriveResourceGroupAsync(allWithSameGoogleId, action, now, cancellationToken);
        }

        if (action == SyncAction.Execute)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return diff;
    }

    private async Task<ResourceSyncDiff> SyncGroupResourceAsync(
        GoogleResource resource,
        SyncAction action,
        Instant now,
        CancellationToken cancellationToken)
    {
        try
        {
            // Expected: team's active members (use Google service email preference)
            var expectedMembers = resource.Team.Members
                .Where(tm => tm.User.GetGoogleServiceEmail() is not null)
                .Select(tm => new { Email = tm.User.GetGoogleServiceEmail(), tm.User.DisplayName })
                .ToList();

            // Subteam member rollup: include child team members for departments
            var childMembers = await GetChildTeamMembersAsync(resource.TeamId, cancellationToken);
            foreach (var cm in childMembers)
            {
                var email = cm.User.GetGoogleServiceEmail();
                if (email is not null && !expectedMembers.Any(m =>
                    NormalizingEmailComparer.Instance.Equals(m.Email, email)))
                {
                    expectedMembers.Add(new { Email = (string?)email, cm.User.DisplayName });
                }
            }

            var expectedEmails = new HashSet<string>(
                expectedMembers.Select(m => m.Email!), NormalizingEmailComparer.Instance);

            // Current: Google Group members via Cloud Identity
            var cloudIdentity = await GetCloudIdentityServiceAsync();
            var saEmail = await GetServiceAccountEmailAsync();
            var currentEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
            // Track membership resource names for deletion (email → "groups/{id}/memberships/{id}")
            var membershipNames = new Dictionary<string, string>(NormalizingEmailComparer.Instance);

            try
            {
                string? pageToken = null;
                do
                {
                    var membersRequest = cloudIdentity.Groups.Memberships.List($"groups/{resource.GoogleId}");
                    membersRequest.PageSize = 200;
                    if (pageToken is not null)
                        membersRequest.PageToken = pageToken;

                    var membersResponse = await membersRequest.ExecuteAsync(cancellationToken);

                    if (membersResponse.Memberships is not null)
                    {
                        foreach (var membership in membersResponse.Memberships)
                        {
                            var email = membership.PreferredMemberKey?.Id;
                            if (!string.IsNullOrEmpty(email))
                            {
                                currentEmails.Add(email);
                                membershipNames[email] = membership.Name;
                            }
                        }
                    }

                    pageToken = membersResponse.NextPageToken;
                } while (!string.IsNullOrEmpty(pageToken));
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code is 404 or 403)
            {
                _logger.LogWarning("Group {GroupId} not found in Google for resource {ResourceId} (HTTP {Code})",
                    resource.GoogleId, resource.Id, ex.Error.Code);
                resource.ErrorMessage = "Group not found in Google";
                return new ResourceSyncDiff
                {
                    ResourceId = resource.Id,
                    ResourceName = resource.Name,
                    ResourceType = resource.ResourceType.ToString(),
                    GoogleId = resource.GoogleId,
                    Url = resource.Url,
                    LinkedTeams = [resource.Team.Name],
                    ErrorMessage = "Group not found in Google"
                };
            }

            // Build member sync status list
            var members = new List<MemberSyncStatus>();
            var teamName = resource.Team.Name;

            foreach (var expected in expectedMembers)
            {
                var state = currentEmails.Contains(expected.Email!)
                    ? MemberSyncState.Correct
                    : MemberSyncState.Missing;
                members.Add(new MemberSyncStatus(expected.Email!, expected.DisplayName, state, [teamName]));
            }

            foreach (var email in currentEmails)
            {
                // Skip the service account — it's the group owner added at creation
                if (string.Equals(email, saEmail, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!expectedEmails.Contains(email))
                {
                    members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, []));
                }
            }

            // Execute if not Preview
            if (action == SyncAction.Execute)
            {
                foreach (var member in members.Where(m => m.State == MemberSyncState.Missing))
                {
                    try
                    {
                        await AddUserToGroupAsync(resource.Id, member.Email, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to add {Email} to group {GroupId}",
                            member.Email, resource.GoogleId);
                    }
                }

                foreach (var member in members.Where(m => m.State == MemberSyncState.Extra))
                {
                    try
                    {
                        await RemoveUserFromGroupAsync(resource.Id, member.Email, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove {Email} from group {GroupId}",
                            member.Email, resource.GoogleId);
                    }
                }
            }

            if (action == SyncAction.Execute)
            {
                resource.LastSyncedAt = now;
                resource.ErrorMessage = null;
            }

            return new ResourceSyncDiff
            {
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType.ToString(),
                GoogleId = resource.GoogleId,
                Url = resource.Url,
                LinkedTeams = [teamName],
                Members = members
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing group resource {ResourceId}", resource.Id);
            if (action == SyncAction.Execute)
                resource.ErrorMessage = ex.Message;
            return new ResourceSyncDiff
            {
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType.ToString(),
                GoogleId = resource.GoogleId,
                Url = resource.Url,
                LinkedTeams = [resource.Team.Name],
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<ResourceSyncDiff> SyncDriveResourceGroupAsync(
        List<GoogleResource> resources,
        SyncAction action,
        Instant now,
        CancellationToken cancellationToken)
    {
        var primary = resources[0];

        try
        {
            // Expected: union of all linked teams' active members
            var membersByEmail = new Dictionary<string, (string DisplayName, List<string> TeamNames)>(
                NormalizingEmailComparer.Instance);

            foreach (var resource in resources)
            {
                var teamName = resource.Team.Name;
                foreach (var tm in resource.Team.Members)
                {
                    var memberEmail = tm.User.GetGoogleServiceEmail();
                    if (memberEmail is null) continue;

                    if (membersByEmail.TryGetValue(memberEmail, out var existing))
                    {
                        if (!existing.TeamNames.Contains(teamName, StringComparer.Ordinal))
                            existing.TeamNames.Add(teamName);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (tm.User.DisplayName, new List<string> { teamName });
                    }
                }

                // Subteam member rollup: include child team members for departments
                var childMembers = await GetChildTeamMembersAsync(resource.TeamId, cancellationToken);
                foreach (var cm in childMembers)
                {
                    var memberEmail = cm.User.GetGoogleServiceEmail();
                    if (memberEmail is null) continue;

                    var childTeamName = cm.Team.Name;
                    if (membersByEmail.TryGetValue(memberEmail, out var existing2))
                    {
                        if (!existing2.TeamNames.Contains(childTeamName, StringComparer.Ordinal))
                            existing2.TeamNames.Add(childTeamName);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (cm.User.DisplayName, new List<string> { childTeamName });
                    }
                }
            }

            var linkedTeams = resources.Select(r => r.Team.Name).Distinct(StringComparer.Ordinal).ToList();

            // Current: Drive permissions
            var drive = await GetDriveServiceAsync();
            var permissions = await ListDrivePermissionsAsync(drive, primary.GoogleId, cancellationToken);
            // All user permissions (direct + inherited) — for checking if member already has access
            var allEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
            // Only direct managed permissions — for detecting removable extras
            var directEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
            // Email → current Google role (reader, writer, etc.)
            var roleByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
            foreach (var perm in permissions)
            {
                if (IsAnyUserPermission(perm))
                {
                    allEmails.Add(perm.EmailAddress);
                    roleByEmail[perm.EmailAddress] = perm.Role;
                }
                if (IsDirectManagedPermission(perm))
                    directEmails.Add(perm.EmailAddress);
            }

            // Build member sync status list
            var members = new List<MemberSyncStatus>();

            foreach (var (email, (displayName, teamNames)) in membersByEmail)
            {
                var state = allEmails.Contains(email)
                    ? MemberSyncState.Correct
                    : MemberSyncState.Missing;
                roleByEmail.TryGetValue(email, out var currentRole);
                members.Add(new MemberSyncStatus(email, displayName, state, teamNames, currentRole));
            }

            var saEmail = await GetServiceAccountEmailAsync();
            foreach (var email in allEmails)
            {
                // Skip the service account — it manages the resources
                if (string.Equals(email, saEmail, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!membersByEmail.ContainsKey(email))
                {
                    var state = directEmails.Contains(email)
                        ? MemberSyncState.Extra
                        : MemberSyncState.Inherited;
                    roleByEmail.TryGetValue(email, out var extraRole);
                    members.Add(new MemberSyncStatus(email, email, state, [], extraRole));
                }
            }

            // Execute if not Preview
            if (action == SyncAction.Execute)
            {
                foreach (var member in members.Where(m => m.State == MemberSyncState.Missing))
                {
                    try
                    {
                        await AddUserToDriveAsync(primary, member.Email, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to grant Drive access to {Email} on {GoogleId}",
                            member.Email, primary.GoogleId);
                    }
                }

                foreach (var member in members.Where(m => m.State == MemberSyncState.Extra))
                {
                    try
                    {
                        // Find the direct managed permission for this user
                        var permToRemove = permissions.FirstOrDefault(p =>
                            IsDirectManagedPermission(p) &&
                            string.Equals(p.EmailAddress, member.Email, StringComparison.OrdinalIgnoreCase));

                        if (permToRemove is null)
                        {
                            _logger.LogInformation(
                                "Skipping removal of {Email} from {GoogleId} — permission is inherited, not direct",
                                member.Email, primary.GoogleId);
                            continue;
                        }

                        await RemoveUserFromDriveAsync(primary, permToRemove.Id, member.Email, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove Drive access for {Email} on {GoogleId}",
                            member.Email, primary.GoogleId);
                    }
                }
            }

            // Update LastSyncedAt on all resource rows with this GoogleId (skip on Preview)
            if (action == SyncAction.Execute)
            {
                foreach (var resource in resources)
                {
                    resource.LastSyncedAt = now;
                    resource.ErrorMessage = null;
                }
            }

            return new ResourceSyncDiff
            {
                ResourceId = primary.Id,
                ResourceName = primary.Name,
                ResourceType = primary.ResourceType.ToString(),
                GoogleId = primary.GoogleId,
                Url = primary.Url,
                PermissionLevel = primary.DrivePermissionLevel.ToString(),
                LinkedTeams = linkedTeams,
                Members = members
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing Drive resource group {GoogleId}", primary.GoogleId);
            if (action == SyncAction.Execute)
            {
                foreach (var resource in resources)
                {
                    resource.ErrorMessage = ex.Message;
                }
            }
            return new ResourceSyncDiff
            {
                ResourceId = primary.Id,
                ResourceName = primary.Name,
                ResourceType = primary.ResourceType.ToString(),
                GoogleId = primary.GoogleId,
                Url = primary.Url,
                PermissionLevel = primary.DrivePermissionLevel.ToString(),
                LinkedTeams = resources.Select(r => r.Team.Name).Distinct(StringComparer.Ordinal).ToList(),
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams
            .Include(t => t.GoogleResources)
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);

        if (team is null)
        {
            _logger.LogWarning("Team {TeamId} not found for EnsureTeamGroupAsync", teamId);
            return GroupLinkResult.Ok();
        }

        var existingGroup = team.GoogleResources
            .FirstOrDefault(r => r.ResourceType == GoogleResourceType.Group && r.IsActive);

        // If prefix was cleared, deactivate any active group resource
        if (team.GoogleGroupPrefix is null)
        {
            if (existingGroup is not null)
            {
                existingGroup.IsActive = false;
                _logger.LogInformation("Deactivated Group resource {ResourceId} for team {TeamId} (prefix cleared)",
                    existingGroup.Id, teamId);

                await _auditLogService.LogAsync(
                    AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                    "Deactivated Google Group resource (prefix cleared)",
                    nameof(GoogleWorkspaceSyncService),
                    relatedEntityId: teamId, relatedEntityType: nameof(Team));

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                _logger.LogDebug("Team {TeamId} has no GoogleGroupPrefix and no active group, nothing to do", teamId);
            }
            return GroupLinkResult.Ok();
        }

        var expectedUrl = $"https://groups.google.com/a/{_settings.Domain}/g/{team.GoogleGroupPrefix}";

        // If existing group matches current prefix, nothing to do
        if (existingGroup is not null &&
            string.Equals(existingGroup.Url, expectedUrl, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Team {TeamId} already has active Group resource {ResourceId} matching prefix",
                teamId, existingGroup.Id);
            return GroupLinkResult.Ok();
        }

        var email = $"{team.GoogleGroupPrefix}@{_settings.Domain}";

        // Check for existing active GoogleResource with this group URL (any team) BEFORE deactivating anything
        var existingActiveByEmail = await _dbContext.GoogleResources
            .Include(r => r.Team)
            .Where(r => r.IsActive && r.ResourceType == GoogleResourceType.Group)
            .Where(r => r.Url != null && r.Url.EndsWith($"/g/{team.GoogleGroupPrefix}"))
            .FirstOrDefaultAsync(cancellationToken);

        if (existingActiveByEmail is not null)
        {
            if (existingActiveByEmail.TeamId == teamId)
                return GroupLinkResult.Error("This group is already linked to this team.");
            else
                return GroupLinkResult.Error($"This group is already linked to team \"{existingActiveByEmail.Team!.Name}\".");
        }

        // Check for inactive resource for this team (reactivation scenario) BEFORE deactivating anything
        var inactiveForTeam = await _dbContext.GoogleResources
            .Where(r => !r.IsActive && r.ResourceType == GoogleResourceType.Group && r.TeamId == teamId)
            .Where(r => r.Url != null && r.Url.EndsWith($"/g/{team.GoogleGroupPrefix}"))
            .FirstOrDefaultAsync(cancellationToken);

        if (inactiveForTeam is not null && !confirmReactivation)
            return GroupLinkResult.NeedsConfirmation(
                "This group was previously linked to this team. Reactivate it?",
                inactiveForTeam.Id);

        if (inactiveForTeam is not null && confirmReactivation)
        {
            inactiveForTeam.IsActive = true;
            inactiveForTeam.LastSyncedAt = _clock.GetCurrentInstant();
            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), inactiveForTeam.Id,
                "Reactivated Google Group resource for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Reactivated Google Group resource {ResourceId} for team {TeamId}",
                inactiveForTeam.Id, teamId);
            return GroupLinkResult.Ok();
        }

        // Validation passed — now safe to deactivate the old resource if prefix changed
        if (existingGroup is not null)
        {
            existingGroup.IsActive = false;
            _logger.LogInformation("Deactivated Group resource {ResourceId} for team {TeamId} (prefix changed to '{Prefix}')",
                existingGroup.Id, teamId, team.GoogleGroupPrefix);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var now = _clock.GetCurrentInstant();

        // Try to find an existing Google Group with this email via Cloud Identity
        try
        {
            var cloudIdentity = await GetCloudIdentityServiceAsync();
            var lookupRequest = cloudIdentity.Groups.Lookup();
            lookupRequest.GroupKeyId = email;
            var lookupResponse = await lookupRequest.ExecuteAsync(cancellationToken);
            var groupId = lookupResponse.Name["groups/".Length..];

            // Group exists in Google — link it
            var resource = new GoogleResource
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                ResourceType = GoogleResourceType.Group,
                GoogleId = groupId,
                Name = team.Name,
                Url = $"https://groups.google.com/a/{_settings.Domain}/g/{team.GoogleGroupPrefix}",
                ProvisionedAt = now,
                LastSyncedAt = now,
                IsActive = true
            };

            _dbContext.GoogleResources.Add(resource);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
                $"Linked existing Google Group '{team.Name}' ({email}) for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Linked existing Google Group {GroupId} ({Email}) to team {TeamId}",
                groupId, email, teamId);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code is 404 or 403)
        {
            // Cloud Identity returns 403 (not 404) when a group doesn't exist.
            // In both cases, fall through to creation — if it's a real permission
            // issue, ProvisionTeamGroupAsync will also fail.
            _logger.LogInformation("Google Group '{Email}' not found (HTTP {Code}), creating for team {TeamId}",
                email, ex.Error.Code, teamId);
            await ProvisionTeamGroupAsync(teamId, email, team.Name, cancellationToken);
        }

        return GroupLinkResult.Ok();
    }

    /// <inheritdoc />
    public async Task RestoreUserToAllTeamsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restoring Google resource access for user {UserId}", userId);

        var user = await _dbContext.Users
            .Include(u => u.TeamMemberships.Where(tm => tm.LeftAt == null))
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
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

    /// <inheritdoc />
    public async Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
        if (mode == SyncMode.None)
        {
            _logger.LogInformation("Google Groups sync is disabled — skipping settings drift check");
            return new GroupSettingsDriftResult
            {
                Skipped = true,
                SkipReason = "Google Groups sync mode is set to None"
            };
        }

        var groupResources = await _dbContext.GoogleResources
            .Include(r => r.Team)
            .Where(r => r.ResourceType == GoogleResourceType.Group && r.IsActive)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Checking group settings for {Count} active Google Groups", groupResources.Count);

        var reports = new List<GroupSettingsDriftReport>();

        foreach (var resource in groupResources)
        {
            var groupEmail = resource.Team.GoogleGroupEmail;
            if (string.IsNullOrEmpty(groupEmail))
            {
                // Fall back to deriving email from URL
                var prefix = resource.Url?.Split("/g/", StringSplitOptions.None).LastOrDefault();
                groupEmail = prefix is not null ? $"{prefix}@{_settings.Domain}" : null;
            }

            if (string.IsNullOrEmpty(groupEmail))
            {
                reports.Add(new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = "Cannot determine group email address"
                });
                continue;
            }

            var report = await CheckSingleGroupSettingsAsync(resource, groupEmail, cancellationToken);
            reports.Add(report);
        }

        return new GroupSettingsDriftResult
        {
            Reports = reports,
            ExpectedSettings = BuildExpectedSettingsDictionary()
        };
    }

    private Google.Apis.Groupssettings.v1.Data.Groups BuildExpectedGroupSettings() => new()
    {
        WhoCanJoin = _settings.Groups.WhoCanJoin,
        WhoCanViewMembership = _settings.Groups.WhoCanViewMembership,
        WhoCanContactOwner = _settings.Groups.WhoCanContactOwner,
        WhoCanPostMessage = _settings.Groups.WhoCanPostMessage,
        WhoCanViewGroup = _settings.Groups.WhoCanViewGroup,
        WhoCanModerateMembers = _settings.Groups.WhoCanModerateMembers,
        AllowExternalMembers = _settings.Groups.AllowExternalMembers ? "true" : "false",
        IsArchived = "true",
        MembersCanPostAsTheGroup = "true",
        IncludeInGlobalAddressList = "true",
        AllowWebPosting = "true",
        MessageModerationLevel = "MODERATE_NONE",
        SpamModerationLevel = "MODERATE",
        EnableCollaborativeInbox = "true"
    };

    private static Dictionary<string, string?> GroupSettingsToDict(Google.Apis.Groupssettings.v1.Data.Groups g) => new(StringComparer.Ordinal)
    {
        ["WhoCanJoin"] = g.WhoCanJoin,
        ["WhoCanViewMembership"] = g.WhoCanViewMembership,
        ["WhoCanContactOwner"] = g.WhoCanContactOwner,
        ["WhoCanPostMessage"] = g.WhoCanPostMessage,
        ["WhoCanViewGroup"] = g.WhoCanViewGroup,
        ["WhoCanModerateMembers"] = g.WhoCanModerateMembers,
        ["AllowExternalMembers"] = g.AllowExternalMembers,
        ["IsArchived"] = g.IsArchived,
        ["MembersCanPostAsTheGroup"] = g.MembersCanPostAsTheGroup,
        ["IncludeInGlobalAddressList"] = g.IncludeInGlobalAddressList,
        ["AllowWebPosting"] = g.AllowWebPosting,
        ["MessageModerationLevel"] = g.MessageModerationLevel,
        ["SpamModerationLevel"] = g.SpamModerationLevel,
        ["EnableCollaborativeInbox"] = g.EnableCollaborativeInbox
    };

    private Dictionary<string, string> BuildExpectedSettingsDictionary() =>
        GroupSettingsToDict(BuildExpectedGroupSettings())
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.Ordinal);

    private async Task<GroupSettingsDriftReport> CheckSingleGroupSettingsAsync(
        GoogleResource resource,
        string groupEmail,
        CancellationToken cancellationToken)
    {
        try
        {
            var groupssettingsService = await GetGroupssettingsServiceAsync();
            var request = groupssettingsService.Groups.Get(groupEmail);
            request.Alt = Google.Apis.Groupssettings.v1.GroupssettingsBaseServiceRequest<Google.Apis.Groupssettings.v1.Data.Groups>.AltEnum.Json;
            var actual = await request.ExecuteAsync(cancellationToken);

            var drifts = new List<GroupSettingDrift>();
            var expected = BuildExpectedSettingsDictionary();
            var actualDict = GroupSettingsToDict(actual);

            foreach (var (key, expectedValue) in expected)
                CompareGroupSetting(drifts, key, expectedValue, actualDict.GetValueOrDefault(key));

            if (drifts.Count > 0)
            {
                _logger.LogWarning("Group '{GroupEmail}' has {DriftCount} setting drift(s): {Drifts}",
                    groupEmail, drifts.Count,
                    string.Join(", ", drifts.Select(d => $"{d.SettingName}: expected={d.ExpectedValue}, actual={d.ActualValue}")));
            }

            return new GroupSettingsDriftReport
            {
                ResourceId = resource.Id,
                GroupEmail = groupEmail,
                GroupName = resource.Name,
                Url = resource.Url,
                Drifts = drifts
            };
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code is 404 or 403)
        {
            _logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})",
                groupEmail, ex.Error.Code);
            return new GroupSettingsDriftReport
            {
                ResourceId = resource.Id,
                GroupEmail = groupEmail,
                GroupName = resource.Name,
                Url = resource.Url,
                ErrorMessage = $"Google API error: {ex.Error.Code} — {ex.Error.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking settings for group '{GroupEmail}'", groupEmail);
            return new GroupSettingsDriftReport
            {
                ResourceId = resource.Id,
                GroupEmail = groupEmail,
                GroupName = resource.Name,
                Url = resource.Url,
                ErrorMessage = $"Error: {ex.Message}"
            };
        }
    }

    private static void CompareGroupSetting(
        List<GroupSettingDrift> drifts,
        string settingName,
        string expectedValue,
        string? actualValue)
    {
        if (actualValue is null) return;
        if (!string.Equals(expectedValue, actualValue, StringComparison.OrdinalIgnoreCase))
        {
            drifts.Add(new GroupSettingDrift(settingName, expectedValue, actualValue));
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        // Settings remediation is always allowed — it doesn't add/remove members,
        // so it's not gated by the sync mode (which controls membership changes).
        try
        {
            var groupssettingsService = await GetGroupssettingsServiceAsync();
            var settings = BuildExpectedGroupSettings();
            var request = groupssettingsService.Groups.Update(settings, groupEmail);
            await request.ExecuteAsync(cancellationToken);

            _logger.LogInformation("Remediated settings for Google Group {GroupEmail}", groupEmail);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceSettingsRemediated, nameof(GoogleResource), Guid.Empty,
                $"Remediated settings for Google Group '{groupEmail}'",
                nameof(GoogleWorkspaceSyncService));

            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remediate settings for Google Group {GroupEmail}", groupEmail);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = await GetDirectoryServiceAsync();

            // Load all Google Workspace users for the domain.
            // Key by normalized email (lowercase + googlemail→gmail) so that lookups match regardless of
            // case or googlemail↔gmail aliasing. Value is the actual primary email from Google (for reporting).
            var googleUsersByNormalizedEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);

            string? pageToken = null;
            do
            {
                var listRequest = directory.Users.List();
                listRequest.Domain = _settings.Domain;
                listRequest.MaxResults = 500;
                if (pageToken is not null)
                    listRequest.PageToken = pageToken;

                var response = await listRequest.ExecuteAsync(cancellationToken);

                if (response.UsersValue is not null)
                {
                    foreach (var googleUser in response.UsersValue)
                    {
                        var primaryEmail = googleUser.PrimaryEmail;
                        if (!string.IsNullOrEmpty(primaryEmail))
                            googleUsersByNormalizedEmail[primaryEmail] = primaryEmail;
                    }
                }

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            // Load all DB users with a non-null email
            var dbUsers = await _dbContext.Users
                .Where(u => u.Email != null)
                .Select(u => new { u.Id, u.DisplayName, u.Email })
                .ToListAsync(cancellationToken);

            var mismatches = new List<Application.DTOs.EmailMismatch>();

            foreach (var dbUser in dbUsers)
            {
                if (dbUser.Email is null) continue;

                // Find the matching Google user by normalized email (handles case and googlemail↔gmail)
                if (!googleUsersByNormalizedEmail.TryGetValue(dbUser.Email, out var matchedGoogleEmail))
                    continue; // User not in Google — not a mismatch we handle here

                // Report if stored email differs from Google's primary email (case difference or googlemail↔gmail)
                if (!string.Equals(dbUser.Email, matchedGoogleEmail, StringComparison.Ordinal))
                {
                    mismatches.Add(new Application.DTOs.EmailMismatch
                    {
                        UserId = dbUser.Id,
                        DisplayName = dbUser.DisplayName,
                        StoredEmail = dbUser.Email,
                        GoogleEmail = matchedGoogleEmail
                    });
                }
            }

            _logger.LogInformation(
                "Email mismatch check complete: {Total} DB users checked, {Count} mismatches found",
                dbUsers.Count, mismatches.Count);

            return new Application.DTOs.EmailBackfillResult
            {
                Mismatches = mismatches,
                TotalUsersChecked = dbUsers.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check email mismatches via Admin SDK");
            return new Application.DTOs.EmailBackfillResult
            {
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = await GetDirectoryServiceAsync();

            // Build lookup: group email prefix → linked Team (active, with GoogleGroupPrefix set)
            var teamsWithGroups = await _dbContext.Teams
                .Where(t => t.IsActive && t.GoogleGroupPrefix != null)
                .ToDictionaryAsync(t => t.GoogleGroupPrefix!, t => t, StringComparer.OrdinalIgnoreCase, cancellationToken);

            // List all groups on the domain (paginated)
            var allGroups = new List<Google.Apis.Admin.Directory.directory_v1.Data.Group>();
            string? pageToken = null;
            do
            {
                var listRequest = directory.Groups.List();
                listRequest.Domain = _settings.Domain;
                listRequest.MaxResults = 200;
                if (pageToken is not null)
                    listRequest.PageToken = pageToken;

                var response = await listRequest.ExecuteAsync(cancellationToken);

                if (response.GroupsValue is not null)
                    allGroups.AddRange(response.GroupsValue);

                pageToken = response.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));

            _logger.LogInformation("Found {Count} Google Groups on domain {Domain}", allGroups.Count, _settings.Domain);

            var expectedSettings = BuildExpectedSettingsDictionary();

            var semaphore = new SemaphoreSlim(5);
            var groupssettingsService = await GetGroupssettingsServiceAsync();

            var tasks = allGroups
                .Where(g => !string.IsNullOrEmpty(g.Email))
                .Select(async group =>
                {
                    var email = group.Email!;
                    var prefix = email.Split('@')[0];
                    teamsWithGroups.TryGetValue(prefix, out var linkedTeam);

                    string? errorMessage = null;
                    var drifts = new List<GroupSettingDrift>();
                    var actualSettings = new Dictionary<string, string>(StringComparer.Ordinal);

                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var req = groupssettingsService.Groups.Get(email);
                        req.Alt = Google.Apis.Groupssettings.v1.GroupssettingsBaseServiceRequest<Google.Apis.Groupssettings.v1.Data.Groups>.AltEnum.Json;
                        var actual = await req.ExecuteAsync(cancellationToken);

                        // Collect ALL non-deprecated actual values
                        void Add(string key, string? val) { if (val is not null) actualSettings[key] = val; }
                        Add("WhoCanJoin", actual.WhoCanJoin);
                        Add("WhoCanViewMembership", actual.WhoCanViewMembership);
                        Add("WhoCanContactOwner", actual.WhoCanContactOwner);
                        Add("WhoCanPostMessage", actual.WhoCanPostMessage);
                        Add("WhoCanViewGroup", actual.WhoCanViewGroup);
                        Add("WhoCanModerateMembers", actual.WhoCanModerateMembers);
                        Add("WhoCanModerateContent", actual.WhoCanModerateContent);
                        Add("WhoCanAssistContent", actual.WhoCanAssistContent);
                        Add("WhoCanDiscoverGroup", actual.WhoCanDiscoverGroup);
                        Add("WhoCanLeaveGroup", actual.WhoCanLeaveGroup);
                        Add("AllowExternalMembers", actual.AllowExternalMembers);
                        Add("AllowWebPosting", actual.AllowWebPosting);
                        Add("IsArchived", actual.IsArchived);
                        Add("ArchiveOnly", actual.ArchiveOnly);
                        Add("MembersCanPostAsTheGroup", actual.MembersCanPostAsTheGroup);
                        Add("IncludeInGlobalAddressList", actual.IncludeInGlobalAddressList);
                        Add("EnableCollaborativeInbox", actual.EnableCollaborativeInbox);
                        Add("MessageModerationLevel", actual.MessageModerationLevel);
                        Add("SpamModerationLevel", actual.SpamModerationLevel);
                        Add("ReplyTo", actual.ReplyTo);
                        Add("CustomReplyTo", actual.CustomReplyTo);
                        Add("IncludeCustomFooter", actual.IncludeCustomFooter);
                        Add("CustomFooterText", actual.CustomFooterText);
                        Add("SendMessageDenyNotification", actual.SendMessageDenyNotification);
                        Add("DefaultMessageDenyNotificationText", actual.DefaultMessageDenyNotificationText);
                        Add("FavoriteRepliesOnTop", actual.FavoriteRepliesOnTop);
                        Add("DefaultSender", actual.DefaultSender);
                        Add("PrimaryLanguage", actual.PrimaryLanguage);

                        // Deprecated settings (still returned by API, shown for visibility)
                        Add("WhoCanInvite", actual.WhoCanInvite);
                        Add("WhoCanAdd", actual.WhoCanAdd);
                        Add("ShowInGroupDirectory", actual.ShowInGroupDirectory);
                        Add("AllowGoogleCommunication", actual.AllowGoogleCommunication);
                        Add("WhoCanApproveMembers", actual.WhoCanApproveMembers);
                        Add("WhoCanBanUsers", actual.WhoCanBanUsers);
                        Add("WhoCanModifyMembers", actual.WhoCanModifyMembers);
                        Add("WhoCanApproveMessages", actual.WhoCanApproveMessages);
                        Add("WhoCanDeleteAnyPost", actual.WhoCanDeleteAnyPost);
                        Add("WhoCanDeleteTopics", actual.WhoCanDeleteTopics);
                        Add("WhoCanLockTopics", actual.WhoCanLockTopics);
                        Add("WhoCanMoveTopicsIn", actual.WhoCanMoveTopicsIn);
                        Add("WhoCanMoveTopicsOut", actual.WhoCanMoveTopicsOut);
                        Add("WhoCanPostAnnouncements", actual.WhoCanPostAnnouncements);
                        Add("WhoCanHideAbuse", actual.WhoCanHideAbuse);
                        Add("WhoCanMakeTopicsSticky", actual.WhoCanMakeTopicsSticky);
                        Add("WhoCanAssignTopics", actual.WhoCanAssignTopics);
                        Add("WhoCanUnassignTopic", actual.WhoCanUnassignTopic);
                        Add("WhoCanTakeTopics", actual.WhoCanTakeTopics);
                        Add("WhoCanMarkDuplicate", actual.WhoCanMarkDuplicate);
                        Add("WhoCanMarkNoResponseNeeded", actual.WhoCanMarkNoResponseNeeded);
                        Add("WhoCanMarkFavoriteReplyOnAnyTopic", actual.WhoCanMarkFavoriteReplyOnAnyTopic);
                        Add("WhoCanMarkFavoriteReplyOnOwnTopic", actual.WhoCanMarkFavoriteReplyOnOwnTopic);
                        Add("WhoCanUnmarkFavoriteReplyOnAnyTopic", actual.WhoCanUnmarkFavoriteReplyOnAnyTopic);
                        Add("WhoCanEnterFreeFormTags", actual.WhoCanEnterFreeFormTags);
                        Add("WhoCanModifyTagsAndCategories", actual.WhoCanModifyTagsAndCategories);
                        Add("WhoCanAddReferences", actual.WhoCanAddReferences);
                        Add("MessageDisplayFont", actual.MessageDisplayFont);
                        Add("MaxMessageBytes", actual.MaxMessageBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture));

                        // Compare against expected (only the enforced settings)
                        // Uses the shared expectedSettings dictionary so creation, detection,
                        // and remediation all agree on the same source of truth.
                        foreach (var (key, expectedValue) in expectedSettings)
                        {
                            actualSettings.TryGetValue(key, out var actualValue);
                            CompareGroupSetting(drifts, key, expectedValue, actualValue);
                        }
                    }
                    catch (Google.GoogleApiException ex)
                    {
                        _logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", email, ex.Error?.Code);
                        errorMessage = $"Google API error: {ex.Error?.Code} — {ex.Error?.Message}";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error fetching settings for group '{GroupEmail}'", email);
                        errorMessage = $"Error: {ex.Message}";
                    }
                    finally
                    {
                        semaphore.Release();
                    }

                    return new DomainGroupInfo
                    {
                        GroupEmail = email,
                        DisplayName = group.Name ?? email,
                        GoogleId = group.Id,
                        MemberCount = (int)(group.DirectMembersCount ?? 0),
                        LinkedTeamName = linkedTeam?.Name,
                        LinkedTeamId = linkedTeam?.Id,
                        ActualSettings = actualSettings,
                        Drifts = drifts,
                        ErrorMessage = errorMessage
                    };
                });

            var groupInfos = (await Task.WhenAll(tasks)).ToList();

            // Sort: linked groups first, then alphabetically by email
            var sorted = groupInfos
                .OrderBy(g => g.LinkedTeamId is null ? 1 : 0)
                .ThenBy(g => g.GroupEmail, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new AllGroupsResult
            {
                Groups = sorted,
                ExpectedSettings = expectedSettings
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate domain groups");
            return new AllGroupsResult
            {
                ErrorMessage = ex.Message
            };
        }
    }
}
