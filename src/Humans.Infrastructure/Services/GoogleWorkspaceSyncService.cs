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
    private readonly IDbContextFactory<HumansDbContext> _dbContextFactory;
    private readonly GoogleWorkspaceSettings _settings;
    private readonly IClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly ISyncSettingsService _syncSettingsService;
    private readonly ILogger<GoogleWorkspaceSyncService> _logger;

    /// <summary>
    /// Throttles concurrent DB access during parallel sync operations.
    /// </summary>
    private static readonly SemaphoreSlim DbSemaphore = new(5);

    private CloudIdentityService? _cloudIdentityService;
    private DirectoryService? _directoryService;
    private DriveService? _driveService;
    private GroupssettingsService? _groupssettingsService;
    private string? _serviceAccountEmail;

    public GoogleWorkspaceSyncService(
        HumansDbContext dbContext,
        IDbContextFactory<HumansDbContext> dbContextFactory,
        IOptions<GoogleWorkspaceSettings> settings,
        IClock clock,
        IAuditLogService auditLogService,
        ISyncSettingsService syncSettingsService,
        ILogger<GoogleWorkspaceSyncService> logger)
    {
        _dbContext = dbContext;
        _dbContextFactory = dbContextFactory;
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

    /// <summary>
    /// Pre-loads child team members for a set of parent team IDs in a single query,
    /// eliminating redundant per-resource DB queries during parallel sync.
    /// </summary>
    private async Task<Dictionary<Guid, List<TeamMember>>> PreloadChildTeamMembersAsync(
        IEnumerable<Guid> parentTeamIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = parentTeamIds.Distinct().ToList();
        if (distinctIds.Count == 0)
            return new Dictionary<Guid, List<TeamMember>>();

        var allChildMembers = await _dbContext.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.User)
            .Include(tm => tm.Team)
            .Where(tm =>
                distinctIds.Contains(tm.Team.ParentTeamId!.Value) &&
                tm.Team.IsActive &&
                tm.LeftAt == null)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, List<TeamMember>>();
        foreach (var id in distinctIds)
            result[id] = [];

        foreach (var tm in allChildMembers)
        {
            if (tm.Team.ParentTeamId is { } parentId && result.ContainsKey(parentId))
                result[parentId].Add(tm);
        }

        return result;
    }

    /// <summary>
    /// Resolves the maximum DrivePermissionLevel for a specific user on a Drive resource,
    /// considering only resources whose teams the user is an active member of.
    /// </summary>
    private async Task<DrivePermissionLevel> ResolvePermissionLevelForUserAsync(
        string googleId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var levels = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.GoogleId == googleId && r.IsActive
                && r.Team.Members.Any(tm => tm.UserId == userId && tm.LeftAt == null))
            .Select(r => r.DrivePermissionLevel)
            .Where(l => l != DrivePermissionLevel.None)
            .ToListAsync(cancellationToken);

        if (levels.Count == 0)
            return DrivePermissionLevel.Contributor;

        return levels.Max();
    }

    /// <summary>
    /// Maps a Google Drive API role string to the corresponding DrivePermissionLevel enum.
    /// Returns null if the role is not recognized.
    /// </summary>
    private static DrivePermissionLevel? ParseApiRole(string? role) => role switch
    {
        "reader" => DrivePermissionLevel.Viewer,
        "commenter" => DrivePermissionLevel.Commenter,
        "writer" => DrivePermissionLevel.Contributor,
        "fileOrganizer" => DrivePermissionLevel.ContentManager,
        "organizer" => DrivePermissionLevel.Manager,
        _ => null
    };

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
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 409)
        {
            _logger.LogDebug("User {UserEmail} is already a member of group {GroupId}", userEmail, resource.GoogleId);
        }
        catch (Google.GoogleApiException ex) when (ex.Error?.Code == 403)
        {
            _logger.LogWarning(
                "Google rejected {UserEmail} for group {GroupName} ({GroupId}) — HTTP 403. " +
                "This typically means the email address does not have a Google account associated with it",
                userEmail, resource.Name, resource.GoogleId);

            // Mark the user's Google email as Rejected so sync stops retrying
            var user = await _dbContext.Users
                .FirstOrDefaultAsync(u => EF.Functions.ILike(u.Email!, userEmail) ||
                    (u.GoogleEmail != null && EF.Functions.ILike(u.GoogleEmail, userEmail)), cancellationToken);
            if (user is not null && user.GoogleEmailStatus != GoogleEmailStatus.Rejected)
            {
                user.GoogleEmailStatus = GoogleEmailStatus.Rejected;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            await _auditLogService.LogGoogleSyncAsync(
                AuditAction.GoogleResourceAccessGranted, groupResourceId,
                $"Google rejected {userEmail} for group {resource.Name} — no Google account for this address (HTTP 403)",
                nameof(GoogleWorkspaceSyncService),
                userEmail, "MEMBER", GoogleSyncSource.ManualSync, success: false);
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
    }

    /// <summary>
    /// GATEWAY METHOD: This is the ONLY way to add a user to a Google Drive resource.
    /// All code paths (outbox, reconciliation, manual sync) must call this method.
    /// Respects SyncSettings — skips if GoogleDrive mode is None.
    /// </summary>
    /// <param name="resource">The Google resource to grant access on.</param>
    /// <param name="userEmail">The user's email address.</param>
    /// <param name="permissionLevelOverride">
    /// Optional override for the permission level. When the same Drive resource is linked
    /// to multiple teams, this should be the resolved maximum level across all teams.
    /// If null, uses the resource's own DrivePermissionLevel.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task AddUserToDriveAsync(
        GoogleResource resource,
        string userEmail,
        DrivePermissionLevel? permissionLevelOverride = null,
        CancellationToken cancellationToken = default)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            _logger.LogDebug("Skipping AddUserToDrive — GoogleDrive sync mode is None");
            return;
        }

        var effectiveLevel = permissionLevelOverride ?? resource.DrivePermissionLevel;
        var drive = await GetDriveServiceAsync();
        var apiRole = effectiveLevel.ToApiRole();
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
            createReq.SendNotificationEmail = false;
            await createReq.ExecuteAsync(cancellationToken);

            await _auditLogService.LogGoogleSyncAsync(
                AuditAction.GoogleResourceAccessGranted, resource.Id,
                $"Granted Drive access ({effectiveLevel}) to {userEmail} ({resource.Name})",
                nameof(GoogleWorkspaceSyncService),
                userEmail, apiRole, GoogleSyncSource.ManualSync, success: true);
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

        // Get current team members (skip users with rejected Google email status)
        var teamMembers = await _dbContext.TeamMembers
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null
                && tm.User.GoogleEmailStatus != GoogleEmailStatus.Rejected)
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

        if (user!.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            _logger.LogDebug("Skipping AddUserToTeamResources for user {UserId} — GoogleEmailStatus is Rejected", userId);
            return;
        }

        // When GoogleEmail differs from the OAuth email, the old email should be removed
        // from Groups to prevent duplicate delivery (e.g., user got @nobodies.team address)
        var previousEmail = !string.Equals(googleEmail, user.Email, StringComparison.OrdinalIgnoreCase)
            ? user.Email
            : null;

        var resources = await _dbContext.GoogleResources
            .Where(r => r.TeamId == teamId && r.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var resource in resources)
        {
            if (resource.ResourceType == GoogleResourceType.Group)
            {
                await AddUserToGroupAsync(resource.Id, googleEmail, cancellationToken);

                // Remove the old OAuth email from the group to avoid duplicate delivery
                if (previousEmail is not null)
                {
                    await RemoveUserFromGroupAsync(resource.Id, previousEmail, cancellationToken);
                }
            }
            else
            {
                // Resolve permission level based on this user's team memberships
                var level = await ResolvePermissionLevelForUserAsync(
                    resource.GoogleId, userId, cancellationToken);
                await AddUserToDriveAsync(resource, googleEmail, level, cancellationToken);
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

                    // Remove the old OAuth email from the parent group too
                    if (previousEmail is not null)
                    {
                        await RemoveUserFromGroupAsync(resource.Id, previousEmail, cancellationToken);
                    }
                }
                else
                {
                    var level = await ResolvePermissionLevelForUserAsync(
                        resource.GoogleId, userId, cancellationToken);
                    await AddUserToDriveAsync(resource, googleEmail, level, cancellationToken);
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

        var now = _clock.GetCurrentInstant();

        // Pre-load child team members for all team IDs in a single query to avoid
        // redundant DB calls during parallel sync (DbContext is not thread-safe).
        var teamIds = resources.Select(r => r.TeamId).Distinct().ToList();
        var childMembersCache = await PreloadChildTeamMembersAsync(teamIds, cancellationToken);

        List<ResourceSyncDiff> diffs;

        if (resourceType == GoogleResourceType.Group)
        {
            // Eagerly initialize Google API clients before parallel execution to avoid
            // non-thread-safe lazy init race in GetCloudIdentityServiceAsync/GetDriveServiceAsync.
            if (resources.Count > 0)
                await GetCloudIdentityServiceAsync();

            // Groups: one group per team — compute diffs in parallel (Google API reads only).
            // Each task gets its own DbContext via factory; semaphore throttles concurrency.
            var diffTasks = resources.Select(async resource =>
            {
                await DbSemaphore.WaitAsync(cancellationToken);
                try
                {
                    return await SyncGroupResourceAsync(resource, SyncAction.Preview, now, childMembersCache, cancellationToken);
                }
                finally
                {
                    DbSemaphore.Release();
                }
            });
            diffs = (await Task.WhenAll(diffTasks)).ToList();

            // Apply mutations sequentially if Execute mode (gateway methods use DbContext)
            if (action == SyncAction.Execute)
            {
                foreach (var (resource, diff) in resources.Zip(diffs))
                {
                    await ExecuteGroupSyncActionsAsync(resource, diff, now, cancellationToken);
                }
            }
        }
        else
        {
            // Eagerly initialize Drive client before parallel execution
            if (resources.Count > 0)
                await GetDriveServiceAsync();

            // Drive resources: group by GoogleId since multiple teams can share one resource.
            // Each task gets its own DbContext via factory; semaphore throttles concurrency.
            var grouped = resources.GroupBy(r => r.GoogleId, StringComparer.Ordinal).ToList();
            var diffTasks = grouped.Select(async group =>
            {
                await DbSemaphore.WaitAsync(cancellationToken);
                try
                {
                    return await SyncDriveResourceGroupAsync(group.ToList(), SyncAction.Preview, now, childMembersCache, cancellationToken);
                }
                finally
                {
                    DbSemaphore.Release();
                }
            });
            diffs = (await Task.WhenAll(diffTasks)).ToList();

            // Apply mutations sequentially if Execute mode (gateway methods use DbContext)
            if (action == SyncAction.Execute)
            {
                foreach (var (group, diff) in grouped.Zip(diffs))
                {
                    await ExecuteDriveSyncActionsAsync(group.ToList(), diff, now, cancellationToken);
                }
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
            diff = await SyncGroupResourceAsync(resource, action, now, childMembersCache: null, cancellationToken);
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

            diff = await SyncDriveResourceGroupAsync(allWithSameGoogleId, action, now, childMembersCache: null, cancellationToken);
        }

        if (action == SyncAction.Execute)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return diff;
    }

    private async Task<ResourceSyncDiff> SyncGroupResourceAsync(
        GoogleResource resource,
        SyncAction action,
        Instant now,
        Dictionary<Guid, List<TeamMember>>? childMembersCache,
        CancellationToken cancellationToken)
    {
        // When running in parallel (childMembersCache != null), create a short-lived DbContext
        // via the factory to avoid sharing the scoped DbContext across concurrent tasks.
        var parallelDbContext = childMembersCache is not null
            ? await _dbContextFactory.CreateDbContextAsync(cancellationToken)
            : null;
        try
        {
            // Expected: team's active members (use Google service email preference)
            // Skip users with Rejected GoogleEmailStatus — their email was permanently
            // rejected by Google (no Google account associated with the address)
            var expectedMembers = resource.Team.Members
                .Where(tm => tm.User.GetGoogleServiceEmail() is not null
                    && tm.User.GoogleEmailStatus != GoogleEmailStatus.Rejected)
                .Select(tm => new { Email = tm.User.GetGoogleServiceEmail(), tm.User.DisplayName, tm.User.Id, tm.User.ProfilePictureUrl })
                .ToList();

            // Subteam member rollup: include child team members for departments
            var childMembers = childMembersCache is not null && childMembersCache.TryGetValue(resource.TeamId, out var cached)
                ? cached
                : await GetChildTeamMembersAsync(resource.TeamId, cancellationToken);
            foreach (var cm in childMembers)
            {
                var email = cm.User.GetGoogleServiceEmail();
                if (email is not null
                    && cm.User.GoogleEmailStatus != GoogleEmailStatus.Rejected
                    && !expectedMembers.Any(m =>
                    NormalizingEmailComparer.Instance.Equals(m.Email, email)))
                {
                    expectedMembers.Add(new { Email = (string?)email, cm.User.DisplayName, cm.User.Id, cm.User.ProfilePictureUrl });
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
                if (action == SyncAction.Execute)
                    resource.ErrorMessage = "Group not found in Google";
                return new ResourceSyncDiff
                {
                    ResourceId = resource.Id,
                    ResourceName = resource.Name,
                    ResourceType = resource.ResourceType.ToString(),
                    GoogleId = resource.GoogleId,
                    Url = resource.Url,
                    LinkedTeams = [new TeamLink(resource.Team.Name, resource.Team.Slug)],
                    ErrorMessage = "Group not found in Google"
                };
            }

            // Build member sync status list
            var members = new List<MemberSyncStatus>();
            var teamLink = new TeamLink(resource.Team.Name, resource.Team.Slug);

            foreach (var expected in expectedMembers)
            {
                var state = currentEmails.Contains(expected.Email!)
                    ? MemberSyncState.Correct
                    : MemberSyncState.Missing;
                members.Add(new MemberSyncStatus(expected.Email!, expected.DisplayName, state, [teamLink],
                    UserId: expected.Id, ProfilePictureUrl: expected.ProfilePictureUrl));
            }

            var extraEmails = currentEmails
                .Where(e => !expectedEmails.Contains(e) &&
                    !string.Equals(e, saEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var extraIdentities = await ResolveExtraEmailIdentitiesAsync(extraEmails, cancellationToken,
                parallelDbContext);

            foreach (var email in extraEmails)
            {
                if (extraIdentities.TryGetValue(email, out var identity))
                {
                    members.Add(new MemberSyncStatus(email, identity.DisplayName, MemberSyncState.Extra, [],
                        UserId: identity.UserId, ProfilePictureUrl: identity.ProfilePictureUrl));
                }
                else
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
                LinkedTeams = [teamLink],
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
                LinkedTeams = [new TeamLink(resource.Team.Name, resource.Team.Slug)],
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (parallelDbContext is not null)
                await parallelDbContext.DisposeAsync();
        }
    }

    /// <summary>
    /// Applies Group sync mutations sequentially after parallel diff collection.
    /// Handles adding/removing members and updating resource metadata.
    /// </summary>
    private async Task ExecuteGroupSyncActionsAsync(
        GoogleResource resource,
        ResourceSyncDiff diff,
        Instant now,
        CancellationToken cancellationToken)
    {
        if (diff.ErrorMessage is not null)
        {
            resource.ErrorMessage = diff.ErrorMessage;
            return;
        }

        foreach (var member in diff.Members.Where(m => m.State == MemberSyncState.Missing))
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

        foreach (var member in diff.Members.Where(m => m.State == MemberSyncState.Extra))
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

        resource.LastSyncedAt = now;
        resource.ErrorMessage = null;
    }

    /// <summary>
    /// Applies Drive sync mutations sequentially after parallel diff collection.
    /// Handles adding/removing permissions and updating resource metadata.
    /// </summary>
    private async Task ExecuteDriveSyncActionsAsync(
        List<GoogleResource> resources,
        ResourceSyncDiff diff,
        Instant now,
        CancellationToken cancellationToken)
    {
        var primary = resources[0];

        if (diff.ErrorMessage is not null)
        {
            foreach (var resource in resources)
                resource.ErrorMessage = diff.ErrorMessage;
            return;
        }

        // Add missing members and fix wrong permission levels
        foreach (var member in diff.Members.Where(m => m.State is MemberSyncState.Missing or MemberSyncState.WrongRole))
        {
            try
            {
                var memberLevel = ParseApiRole(member.ExpectedRole) ?? DrivePermissionLevel.Contributor;
                await AddUserToDriveAsync(primary, member.Email, memberLevel, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to grant Drive access to {Email} on {GoogleId}",
                    member.Email, primary.GoogleId);
            }
        }

        // Remove extra members — need to re-fetch permissions for permission IDs
        var extraMembers = diff.Members.Where(m => m.State == MemberSyncState.Extra).ToList();
        if (extraMembers.Count > 0)
        {
            var drive = await GetDriveServiceAsync();
            var permissions = await ListDrivePermissionsAsync(drive, primary.GoogleId, cancellationToken);

            foreach (var member in extraMembers)
            {
                try
                {
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

        foreach (var resource in resources)
        {
            resource.LastSyncedAt = now;
            resource.ErrorMessage = null;
        }
    }

    private async Task<ResourceSyncDiff> SyncDriveResourceGroupAsync(
        List<GoogleResource> resources,
        SyncAction action,
        Instant now,
        Dictionary<Guid, List<TeamMember>>? childMembersCache,
        CancellationToken cancellationToken)
    {
        // When running in parallel (childMembersCache != null), create a short-lived DbContext
        // via the factory to avoid sharing the scoped DbContext across concurrent tasks.
        var parallelDbContext = childMembersCache is not null
            ? await _dbContextFactory.CreateDbContextAsync(cancellationToken)
            : null;
        var primary = resources[0];
        // Build a lookup from team slug to permission level for per-member resolution
        var levelByTeamSlug = new Dictionary<string, DrivePermissionLevel>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var slug = resource.Team.Slug;
            if (!levelByTeamSlug.TryGetValue(slug, out var existing) || resource.DrivePermissionLevel > existing)
                levelByTeamSlug[slug] = resource.DrivePermissionLevel;
        }

        try
        {
            // Expected: union of all linked teams' active members
            var membersByEmail = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl, List<TeamLink> TeamLinks)>(
                NormalizingEmailComparer.Instance);

            foreach (var resource in resources)
            {
                var level = resource.DrivePermissionLevel is DrivePermissionLevel.None
                    ? null : resource.DrivePermissionLevel.ToString();
                var teamLink = new TeamLink(resource.Team.Name, resource.Team.Slug, level);
                foreach (var tm in resource.Team.Members)
                {
                    var memberEmail = tm.User.GetGoogleServiceEmail();
                    if (memberEmail is null || tm.User.GoogleEmailStatus == GoogleEmailStatus.Rejected)
                        continue;

                    if (membersByEmail.TryGetValue(memberEmail, out var existing))
                    {
                        if (!existing.TeamLinks.Any(tl => string.Equals(tl.Name, teamLink.Name, StringComparison.Ordinal)))
                            existing.TeamLinks.Add(teamLink);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (tm.User.DisplayName, tm.User.Id, tm.User.ProfilePictureUrl, new List<TeamLink> { teamLink });
                    }
                }

                // Subteam member rollup: include child team members for departments
                var childMembers = childMembersCache is not null && childMembersCache.TryGetValue(resource.TeamId, out var cached)
                    ? cached
                    : await GetChildTeamMembersAsync(resource.TeamId, cancellationToken);
                foreach (var cm in childMembers)
                {
                    var memberEmail = cm.User.GetGoogleServiceEmail();
                    if (memberEmail is null || cm.User.GoogleEmailStatus == GoogleEmailStatus.Rejected)
                        continue;

                    var childTeamLink = new TeamLink(cm.Team.Name, cm.Team.Slug, level);
                    if (membersByEmail.TryGetValue(memberEmail, out var existing2))
                    {
                        if (!existing2.TeamLinks.Any(tl => string.Equals(tl.Name, childTeamLink.Name, StringComparison.Ordinal)))
                            existing2.TeamLinks.Add(childTeamLink);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (cm.User.DisplayName, cm.User.Id, cm.User.ProfilePictureUrl, new List<TeamLink> { childTeamLink });
                    }
                }
            }

            var linkedTeams = resources.Select(r => new TeamLink(r.Team.Name, r.Team.Slug,
                    r.DrivePermissionLevel is DrivePermissionLevel.None ? null : r.DrivePermissionLevel.ToString()))
                .DistinctBy(tl => tl.Slug, StringComparer.Ordinal).ToList();

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

            foreach (var (email, (displayName, userId, profilePictureUrl, teamLinks)) in membersByEmail)
            {
                // Resolve this member's expected level from their specific team memberships
                var memberMaxLevel = DrivePermissionLevel.None;
                foreach (var tl in teamLinks)
                {
                    if (levelByTeamSlug.TryGetValue(tl.Slug, out var tlLevel) && tlLevel > memberMaxLevel)
                        memberMaxLevel = tlLevel;
                }
                var memberExpectedRole = memberMaxLevel > DrivePermissionLevel.None
                    ? memberMaxLevel.ToApiRole() : null;

                MemberSyncState state;
                roleByEmail.TryGetValue(email, out var currentRole);

                if (!allEmails.Contains(email))
                {
                    state = MemberSyncState.Missing;
                }
                else if (!directEmails.Contains(email))
                {
                    // Member has access but only via inherited Shared Drive permission —
                    // the system can't manage this, so mark as Inherited
                    state = MemberSyncState.Inherited;
                }
                else
                {
                    // Member has a direct permission — check if the level matches their expected
                    var currentLevel = ParseApiRole(currentRole);
                    state = currentLevel.HasValue && currentLevel.Value < memberMaxLevel
                        ? MemberSyncState.WrongRole
                        : MemberSyncState.Correct;
                }

                members.Add(new MemberSyncStatus(email, displayName, state, teamLinks, currentRole, memberExpectedRole,
                    UserId: userId, ProfilePictureUrl: profilePictureUrl));
            }

            var saEmail = await GetServiceAccountEmailAsync();
            var nonMemberEmails = allEmails
                .Where(e => !membersByEmail.ContainsKey(e) &&
                    !string.Equals(e, saEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var extraIdentities = await ResolveExtraEmailIdentitiesAsync(nonMemberEmails, cancellationToken,
                parallelDbContext);

            foreach (var email in nonMemberEmails)
            {
                var state = directEmails.Contains(email)
                    ? MemberSyncState.Extra
                    : MemberSyncState.Inherited;
                roleByEmail.TryGetValue(email, out var extraRole);

                if (extraIdentities.TryGetValue(email, out var identity))
                {
                    members.Add(new MemberSyncStatus(email, identity.DisplayName, state, [], extraRole,
                        UserId: identity.UserId, ProfilePictureUrl: identity.ProfilePictureUrl));
                }
                else
                {
                    members.Add(new MemberSyncStatus(email, email, state, [], extraRole));
                }
            }

            // Execute if not Preview
            if (action == SyncAction.Execute)
            {
                // Add missing members and fix wrong permission levels
                foreach (var member in members.Where(m => m.State is MemberSyncState.Missing or MemberSyncState.WrongRole))
                {
                    try
                    {
                        var memberLevel = ParseApiRole(member.ExpectedRole) ?? DrivePermissionLevel.Contributor;
                        await AddUserToDriveAsync(primary, member.Email, memberLevel, cancellationToken);
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
                LinkedTeams = resources.Select(r => new TeamLink(r.Team.Name, r.Team.Slug,
                        r.DrivePermissionLevel is DrivePermissionLevel.None ? null : r.DrivePermissionLevel.ToString()))
                    .DistinctBy(tl => tl.Slug, StringComparer.Ordinal).ToList(),
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (parallelDbContext is not null)
                await parallelDbContext.DisposeAsync();
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
            .Where(r => r.Url != null && EF.Functions.ILike(r.Url!, expectedUrl))
            .OrderBy(r => r.Id)
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
            .Where(r => r.Url != null && EF.Functions.ILike(r.Url!, expectedUrl))
            .OrderBy(r => r.Id)
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
            return GroupLinkResult.Ok();
        }

        // Validation passed — now safe to deactivate the old resource if prefix changed
        if (existingGroup is not null)
        {
            existingGroup.IsActive = false;

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
                var prefix = resource.Url?.Split("/g/").LastOrDefault();
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
                        LinkedTeamSlug = linkedTeam?.Slug,
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

    /// <inheritdoc />
    public async Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        var driveResources = await _dbContext.GoogleResources
            .Where(r => r.ResourceType == GoogleResourceType.DriveFolder && r.IsActive)
            .ToListAsync(cancellationToken);

        if (driveResources.Count == 0)
            return 0;

        var drive = await GetDriveServiceAsync();
        var updatedCount = 0;

        foreach (var resource in driveResources)
        {
            try
            {
                var fullPath = await ResolveDriveFolderPathAsync(drive, resource.GoogleId, cancellationToken);
                if (fullPath is not null && !string.Equals(resource.Name, fullPath, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Drive folder path changed for resource {ResourceId}: '{OldName}' -> '{NewName}'",
                        resource.Id, resource.Name, fullPath);
                    resource.Name = fullPath;
                    updatedCount++;
                }
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
            {
                _logger.LogWarning("Drive folder {GoogleId} not found (resource {ResourceId}) — may have been deleted",
                    resource.GoogleId, resource.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve Drive folder path for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        if (updatedCount > 0)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return updatedCount;
    }

    /// <summary>
    /// Resolves the full path of a Drive folder by walking the parent chain.
    /// Returns a path like "Shared Drive / Department / Subfolder".
    /// </summary>
    private async Task<string?> ResolveDriveFolderPathAsync(
        DriveService drive, string fileId, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var currentId = fileId;
        // Safety limit to prevent infinite loops on circular references
        const int maxDepth = 20;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var request = drive.Files.Get(currentId);
            request.SupportsAllDrives = true;
            request.Fields = "name, parents, driveId";
            var file = await request.ExecuteAsync(cancellationToken);

            // When the file IS the shared drive root, Files.Get returns "Drive" as the name.
            // Use Drives.Get to get the actual drive name.
            if (!string.IsNullOrEmpty(file.DriveId)
                && string.Equals(currentId, file.DriveId, StringComparison.Ordinal))
            {
                try
                {
                    var driveInfo = await drive.Drives.Get(file.DriveId).ExecuteAsync(cancellationToken);
                    segments.Add(driveInfo.Name);
                }
                catch (Google.GoogleApiException ex)
                {
                    _logger.LogDebug(ex, "Service account cannot access Shared Drive metadata for {DriveId}", file.DriveId);
                    segments.Add(file.Name);
                }
                break;
            }

            segments.Add(file.Name);

            if (file.Parents is null || file.Parents.Count == 0)
                break;

            currentId = file.Parents[0];
        }

        if (segments.Count == 0)
            return null;

        segments.Reverse();
        return string.Join(" / ", segments);
    }

    /// <inheritdoc />
    public async Task SetInheritedPermissionsDisabledAsync(string googleFileId, bool restrict, CancellationToken cancellationToken = default)
    {
        var drive = await GetDriveServiceAsync();
        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            InheritedPermissionsDisabled = restrict
        };
        var updateRequest = drive.Files.Update(fileMetadata, googleFileId);
        updateRequest.SupportsAllDrives = true;
        updateRequest.Fields = "id, inheritedPermissionsDisabled";
        await updateRequest.ExecuteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default)
    {
        var restrictedResources = await _dbContext.GoogleResources
            .Where(r => r.RestrictInheritedAccess
                && r.ResourceType == GoogleResourceType.DriveFolder
                && r.IsActive)
            .ToListAsync(cancellationToken);

        if (restrictedResources.Count == 0)
            return 0;

        var drive = await GetDriveServiceAsync();
        var correctedCount = 0;

        foreach (var resource in restrictedResources)
        {
            try
            {
                var getRequest = drive.Files.Get(resource.GoogleId);
                getRequest.SupportsAllDrives = true;
                getRequest.Fields = "id, inheritedPermissionsDisabled";
                var file = await getRequest.ExecuteAsync(cancellationToken);

                if (file.InheritedPermissionsDisabled != true)
                {
                    _logger.LogWarning(
                        "Inherited access drift detected for resource {ResourceId} ({GoogleId}): " +
                        "inheritedPermissionsDisabled is {Actual}, expected true. Correcting.",
                        resource.Id, resource.GoogleId, file.InheritedPermissionsDisabled);

                    await SetInheritedPermissionsDisabledAsync(resource.GoogleId, true, cancellationToken);

                    await _auditLogService.LogAsync(
                        AuditAction.GoogleResourceInheritanceDriftCorrected,
                        nameof(GoogleResource), resource.Id,
                        $"Corrected inherited access drift for Drive folder '{resource.Name}' — " +
                        "re-disabled inherited permissions",
                        "GoogleResourceReconciliationJob");

                    correctedCount++;
                }
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code == 404)
            {
                _logger.LogWarning(
                    "Drive folder {GoogleId} not found (resource {ResourceId}) during inherited access check — may have been deleted",
                    resource.GoogleId, resource.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to check/enforce inherited access restriction for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        if (correctedCount > 0)
            await _dbContext.SaveChangesAsync(cancellationToken);

        return correctedCount;
    }

    /// <summary>
    /// Resolves extra emails against UserEmail records to get display names, user IDs, and profile pictures.
    /// Returns a lookup keyed by email (case-insensitive) with user identity info.
    /// </summary>
    /// <param name="emails">The emails to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="dbContext">Optional DbContext for parallel execution (uses scoped _dbContext if null).</param>
    private async Task<Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>>
        ResolveExtraEmailIdentitiesAsync(IEnumerable<string> emails, CancellationToken cancellationToken,
            HumansDbContext? dbContext = null)
    {
        var emailList = emails.ToList();
        if (emailList.Count == 0)
            return new Dictionary<string, (string, Guid, string?)>(NormalizingEmailComparer.Instance);

        var ctx = dbContext ?? _dbContext;
        var matches = await ctx.UserEmails
            .AsNoTracking()
            .Include(ue => ue.User)
            .Where(ue => emailList.Contains(ue.Email))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>(
            NormalizingEmailComparer.Instance);

        foreach (var match in matches)
        {
            // First match wins (a user might have multiple matching emails)
            result.TryAdd(match.Email, (match.User.DisplayName, match.UserId, match.User.ProfilePictureUrl));
        }

        return result;
    }
}
