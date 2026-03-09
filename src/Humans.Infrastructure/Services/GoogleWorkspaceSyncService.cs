using Google.Apis.Admin.Directory.directory_v1;
using Google.Apis.Admin.Directory.directory_v1.Data;
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
    private GroupssettingsService? _groupssettingsService;

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

    private async Task<GroupssettingsService> GetGroupssettingsServiceAsync()
    {
        if (_groupssettingsService != null)
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

        // Apply configured group settings
        try
        {
            var groupssettingsService = await GetGroupssettingsServiceAsync();
            var groupSettings = new Google.Apis.Groupssettings.v1.Data.Groups
            {
                WhoCanJoin = _settings.Groups.WhoCanJoin,
                WhoCanViewMembership = _settings.Groups.WhoCanViewMembership,
                WhoCanContactOwner = _settings.Groups.WhoCanContactOwner,
                WhoCanPostMessage = _settings.Groups.WhoCanPostMessage,
                WhoCanViewGroup = _settings.Groups.WhoCanViewGroup,
                WhoCanModerateMembers = _settings.Groups.WhoCanModerateMembers,
                AllowExternalMembers = _settings.Groups.AllowExternalMembers ? "true" : "false",
            };
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
        // Per-user outbox flow: add-only. Bulk removals are handled by
        // SyncResourcesByTypeAsync with SyncAction.AddAndRemove.
        _logger.LogInformation("Skipping per-user Google Group removal for {UserEmail} from {GroupResourceId} (outbox flow is add-only)",
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
        // Per-user outbox flow: add-only. Bulk removals are handled by
        // SyncResourcesByTypeAsync with SyncAction.AddAndRemove.
        _logger.LogInformation("Skipping per-user Google resource removal for user {UserId} from team {TeamId} (outbox flow is add-only)",
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

        if (action != SyncAction.Preview)
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

        if (resource == null)
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

        if (action != SyncAction.Preview)
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
            // Expected: team's active members
            var expectedMembers = resource.Team.Members
                .Where(tm => tm.User.Email != null)
                .Select(tm => new { tm.User.Email, tm.User.DisplayName })
                .ToList();
            var expectedEmails = new HashSet<string>(
                expectedMembers.Select(m => m.Email!), StringComparer.OrdinalIgnoreCase);

            // Current: Google Group members
            var directory = await GetDirectoryServiceAsync();
            var currentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string? pageToken = null;
                do
                {
                    var membersRequest = directory.Members.List(resource.GoogleId);
                    membersRequest.MaxResults = 200;
                    if (pageToken != null)
                        membersRequest.PageToken = pageToken;

                    var membersResponse = await membersRequest.ExecuteAsync(cancellationToken);

                    if (membersResponse.MembersValue != null)
                    {
                        foreach (var member in membersResponse.MembersValue)
                        {
                            if (!string.IsNullOrEmpty(member.Email))
                                currentEmails.Add(member.Email);
                        }
                    }

                    pageToken = membersResponse.NextPageToken;
                } while (!string.IsNullOrEmpty(pageToken));
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
            {
                _logger.LogWarning("Group {GroupId} not found in Google for resource {ResourceId}",
                    resource.GoogleId, resource.Id);
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
                if (!expectedEmails.Contains(email))
                {
                    members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, []));
                }
            }

            // Execute if not Preview
            if (action != SyncAction.Preview)
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

                if (action == SyncAction.AddAndRemove)
                {
                    foreach (var member in members.Where(m => m.State == MemberSyncState.Extra))
                    {
                        try
                        {
                            await directory.Members.Delete(resource.GoogleId, member.Email)
                                .ExecuteAsync(cancellationToken);

                            await _auditLogService.LogGoogleSyncAsync(
                                AuditAction.GoogleResourceAccessRevoked, resource.Id,
                                $"Removed {member.Email} from Google Group ({resource.Name})",
                                nameof(GoogleWorkspaceSyncService),
                                member.Email, "MEMBER", GoogleSyncSource.ManualSync, success: true);

                            _logger.LogInformation("Removed {Email} from group {GroupId}",
                                member.Email, resource.GoogleId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to remove {Email} from group {GroupId}",
                                member.Email, resource.GoogleId);
                        }
                    }
                }
            }

            if (action != SyncAction.Preview)
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
            if (action != SyncAction.Preview)
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
                StringComparer.OrdinalIgnoreCase);

            foreach (var resource in resources)
            {
                var teamName = resource.Team.Name;
                foreach (var tm in resource.Team.Members)
                {
                    if (tm.User.Email == null) continue;

                    if (membersByEmail.TryGetValue(tm.User.Email, out var existing))
                    {
                        if (!existing.TeamNames.Contains(teamName, StringComparer.Ordinal))
                            existing.TeamNames.Add(teamName);
                    }
                    else
                    {
                        membersByEmail[tm.User.Email] = (tm.User.DisplayName, new List<string> { teamName });
                    }
                }
            }

            var linkedTeams = resources.Select(r => r.Team.Name).Distinct(StringComparer.Ordinal).ToList();

            // Current: Drive permissions
            var drive = await GetDriveServiceAsync();
            var permissions = await ListDrivePermissionsAsync(drive, primary.GoogleId, cancellationToken);
            var currentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var perm in permissions)
            {
                if (IsAnyUserPermission(perm))
                    currentEmails.Add(perm.EmailAddress);
            }

            // Build member sync status list
            var members = new List<MemberSyncStatus>();

            foreach (var (email, (displayName, teamNames)) in membersByEmail)
            {
                var state = currentEmails.Contains(email)
                    ? MemberSyncState.Correct
                    : MemberSyncState.Missing;
                members.Add(new MemberSyncStatus(email, displayName, state, teamNames));
            }

            foreach (var email in currentEmails)
            {
                if (!membersByEmail.ContainsKey(email))
                {
                    members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, []));
                }
            }

            // Execute if not Preview
            if (action != SyncAction.Preview)
            {
                foreach (var member in members.Where(m => m.State == MemberSyncState.Missing))
                {
                    try
                    {
                        var permission = new Google.Apis.Drive.v3.Data.Permission
                        {
                            Type = "user",
                            Role = "writer",
                            EmailAddress = member.Email
                        };

                        var createReq = drive.Permissions.Create(permission, primary.GoogleId);
                        createReq.SupportsAllDrives = true;
                        await createReq.ExecuteAsync(cancellationToken);

                        await _auditLogService.LogGoogleSyncAsync(
                            AuditAction.GoogleResourceAccessGranted, primary.Id,
                            $"Granted Drive access to {member.Email} ({primary.Name})",
                            nameof(GoogleWorkspaceSyncService),
                            member.Email, "writer", GoogleSyncSource.ManualSync, success: true);

                        _logger.LogInformation("Granted Drive access to {Email} on {GoogleId}",
                            member.Email, primary.GoogleId);
                    }
                    catch (Google.GoogleApiException ex) when (ex.Error?.Code == 400)
                    {
                        _logger.LogDebug("Permission already exists for {Email} on {GoogleId}",
                            member.Email, primary.GoogleId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to grant Drive access to {Email} on {GoogleId}",
                            member.Email, primary.GoogleId);
                    }
                }

                if (action == SyncAction.AddAndRemove)
                {
                    foreach (var member in members.Where(m => m.State == MemberSyncState.Extra))
                    {
                        try
                        {
                            // Find the direct managed permission for this user
                            var permToRemove = permissions.FirstOrDefault(p =>
                                IsDirectManagedPermission(p) &&
                                string.Equals(p.EmailAddress, member.Email, StringComparison.OrdinalIgnoreCase));

                            if (permToRemove == null)
                            {
                                _logger.LogInformation(
                                    "Skipping removal of {Email} from {GoogleId} — permission is inherited, not direct",
                                    member.Email, primary.GoogleId);
                                continue;
                            }

                            var deleteReq = drive.Permissions.Delete(primary.GoogleId, permToRemove.Id);
                            deleteReq.SupportsAllDrives = true;
                            await deleteReq.ExecuteAsync(cancellationToken);

                            await _auditLogService.LogGoogleSyncAsync(
                                AuditAction.GoogleResourceAccessRevoked, primary.Id,
                                $"Removed Drive access for {member.Email} ({primary.Name})",
                                nameof(GoogleWorkspaceSyncService),
                                member.Email, "writer", GoogleSyncSource.ManualSync, success: true);

                            _logger.LogInformation("Removed Drive access for {Email} on {GoogleId}",
                                member.Email, primary.GoogleId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to remove Drive access for {Email} on {GoogleId}",
                                member.Email, primary.GoogleId);
                        }
                    }
                }
            }

            // Update LastSyncedAt on all resource rows with this GoogleId (skip on Preview)
            if (action != SyncAction.Preview)
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
                LinkedTeams = linkedTeams,
                Members = members
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing Drive resource group {GoogleId}", primary.GoogleId);
            if (action != SyncAction.Preview)
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
                LinkedTeams = resources.Select(r => r.Team.Name).Distinct(StringComparer.Ordinal).ToList(),
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task EnsureTeamGroupAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams
            .Include(t => t.GoogleResources)
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);

        if (team == null)
        {
            _logger.LogWarning("Team {TeamId} not found for EnsureTeamGroupAsync", teamId);
            return;
        }

        if (team.GoogleGroupPrefix == null)
        {
            _logger.LogDebug("Team {TeamId} has no GoogleGroupPrefix, skipping group ensure", teamId);
            return;
        }

        // Check if an active Group resource already exists
        var existingGroup = team.GoogleResources
            .FirstOrDefault(r => r.ResourceType == GoogleResourceType.Group && r.IsActive);

        if (existingGroup != null)
        {
            _logger.LogDebug("Team {TeamId} already has active Group resource {ResourceId}", teamId, existingGroup.Id);
            return;
        }

        var email = $"{team.GoogleGroupPrefix}@{_settings.Domain}";
        var now = _clock.GetCurrentInstant();

        // Try to find an existing Google Group with this email
        try
        {
            var directory = await GetDirectoryServiceAsync();
            var existingGoogleGroup = await directory.Groups.Get(email).ExecuteAsync(cancellationToken);

            // Group exists in Google — link it
            var resource = new GoogleResource
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                ResourceType = GoogleResourceType.Group,
                GoogleId = existingGoogleGroup.Id,
                Name = team.Name,
                Url = $"https://groups.google.com/a/{_settings.Domain}/g/{team.GoogleGroupPrefix}",
                ProvisionedAt = now,
                LastSyncedAt = now,
                IsActive = true
            };

            _dbContext.GoogleResources.Add(resource);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, "GoogleResource", resource.Id,
                $"Linked existing Google Group '{team.Name}' ({email}) for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Linked existing Google Group {GroupId} ({Email}) to team {TeamId}",
                existingGoogleGroup.Id, email, teamId);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            // Group doesn't exist — create it
            _logger.LogInformation("Google Group '{Email}' not found, creating for team {TeamId}", email, teamId);
            await ProvisionTeamGroupAsync(teamId, email, team.Name, cancellationToken);
        }
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
