using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Application-layer implementation of <see cref="IGoogleSyncService"/>. Owns
/// the Google Workspace reconciliation flow: provisioning Drive folders and
/// Google Groups, keeping their memberships in sync with team membership,
/// running per-type reconciliation (preview + execute), and detecting /
/// remediating group-settings drift.
/// </summary>
/// <remarks>
/// <para>
/// Migrated to <c>Humans.Application</c> under §15 Part 2b (issue #575).
/// Google SDK access is routed through shape-neutral connector interfaces
/// (<see cref="IGoogleDirectoryClient"/>, <see cref="IGoogleDrivePermissionsClient"/>,
/// <see cref="IGoogleGroupMembershipClient"/>, <see cref="IGoogleGroupProvisioningClient"/>,
/// <see cref="ITeamResourceGoogleClient"/>) so this service never imports
/// <c>Google.Apis.*</c> (design-rules §13).
/// </para>
/// <para>
/// Cross-section database reads flow through sibling service interfaces:
/// <see cref="ITeamService"/> for team graph (members, child members,
/// parent lookups), <see cref="IUserService"/> for User rows,
/// <see cref="IUserEmailService"/> for extra-email identity resolution,
/// <see cref="ITeamResourceService"/> for <see cref="GoogleResource"/>
/// reads, and <see cref="IGoogleResourceRepository"/> for narrow writes to
/// the sibling-owned <c>google_resources</c> table (design-rules §2c, §6).
/// </para>
/// <para>
/// Outbox-event reads are routed through
/// <see cref="IGoogleSyncOutboxRepository"/> for the failed-event count that
/// the notification meter surfaces. Team-resource soft-delete after a sync
/// pass is routed through <see cref="ITeamResourceService"/>; an
/// <see cref="IServiceProvider"/> lazy resolve is used to break the
/// construction cycle between <c>GoogleWorkspaceSyncService</c> and
/// <c>TeamResourceService</c>.
/// </para>
/// </remarks>
public sealed class GoogleWorkspaceSyncService : IGoogleSyncService
{
    private readonly IGoogleGroupMembershipClient _groupMembership;
    private readonly IGoogleGroupProvisioningClient _groupProvisioning;
    private readonly IGoogleDrivePermissionsClient _drivePermissions;
    private readonly IGoogleDirectoryClient _directory;
    private readonly ITeamResourceGoogleClient _teamResourceClient;
    private readonly IGoogleResourceRepository _resourceRepository;
    private readonly IGoogleSyncOutboxRepository _googleSyncOutboxRepository;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly ISyncSettingsService _syncSettingsService;
    private readonly GoogleWorkspaceOptions _options;
    private readonly IClock _clock;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GoogleWorkspaceSyncService> _logger;

    public GoogleWorkspaceSyncService(
        IGoogleGroupMembershipClient groupMembership,
        IGoogleGroupProvisioningClient groupProvisioning,
        IGoogleDrivePermissionsClient drivePermissions,
        IGoogleDirectoryClient directory,
        ITeamResourceGoogleClient teamResourceClient,
        IGoogleResourceRepository resourceRepository,
        IGoogleSyncOutboxRepository googleSyncOutboxRepository,
        ITeamService teamService,
        IUserService userService,
        IUserEmailService userEmailService,
        IAuditLogService auditLogService,
        ISyncSettingsService syncSettingsService,
        IOptions<GoogleWorkspaceOptions> options,
        IClock clock,
        IServiceProvider serviceProvider,
        ILogger<GoogleWorkspaceSyncService> logger)
    {
        _groupMembership = groupMembership;
        _groupProvisioning = groupProvisioning;
        _drivePermissions = drivePermissions;
        _directory = directory;
        _teamResourceClient = teamResourceClient;
        _resourceRepository = resourceRepository;
        _googleSyncOutboxRepository = googleSyncOutboxRepository;
        _teamService = teamService;
        _userService = userService;
        _userEmailService = userEmailService;
        _auditLogService = auditLogService;
        _syncSettingsService = syncSettingsService;
        _options = options.Value;
        _clock = clock;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // ==========================================================================
    // Provisioning — Drive folders and Google Groups
    // ==========================================================================

    /// <inheritdoc />
    public async Task<GoogleResource> ProvisionTeamFolderAsync(
        Guid teamId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: return any existing active folder for the team.
        var existingActive = await _resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var existing = existingActive.FirstOrDefault(r => r.ResourceType == GoogleResourceType.DriveFolder);
        if (existing is not null)
        {
            _logger.LogInformation("Team {TeamId} already has active Drive folder {FolderId}", teamId, existing.GoogleId);
            return existing;
        }

        _logger.LogInformation("Provisioning Drive folder '{FolderName}' for team {TeamId}", folderName, teamId);

        var create = await _drivePermissions.CreateFolderAsync(folderName, _options.TeamFoldersParentId, cancellationToken);
        if (create.Folder is null)
        {
            var err = create.Error;
            throw new InvalidOperationException(
                $"Google Drive folder create failed (HTTP {err?.StatusCode}): {err?.RawMessage}");
        }

        var now = _clock.GetCurrentInstant();
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.DriveFolder,
            GoogleId = create.Folder.Id ?? string.Empty,
            Name = create.Folder.Name ?? folderName,
            Url = create.Folder.WebViewLink,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        await _resourceRepository.AddAsync(resource, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Provisioned Drive folder '{resource.Name}' for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

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

        var create = await _groupProvisioning.CreateGroupAsync(
            groupEmail,
            groupName,
            $"Mailing list for {groupName} team",
            cancellationToken);

        if (create.GroupNumericId is null)
        {
            var err = create.Error;
            throw new InvalidOperationException(
                $"Google Group create failed for {groupEmail} (HTTP {err?.StatusCode}): {err?.RawMessage}");
        }

        // Apply configured group settings — non-fatal on failure (group was created).
        var settingsError = await _groupProvisioning.UpdateGroupSettingsAsync(
            groupEmail, BuildExpectedGroupSettings(), cancellationToken);

        if (settingsError is not null)
        {
            _logger.LogWarning(
                "Failed to apply group settings to '{GroupEmail}' (HTTP {Code}: {Message}). Group was created with Google defaults",
                groupEmail, settingsError.StatusCode, settingsError.RawMessage);
        }
        else
        {
            _logger.LogInformation("Applied group settings to '{GroupEmail}'", groupEmail);
        }

        var now = _clock.GetCurrentInstant();
        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = create.GroupNumericId,
            Name = groupName,
            Url = $"https://groups.google.com/a/{_options.Domain}/g/{groupEmail.Split('@')[0]}",
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        await _resourceRepository.AddAsync(resource, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Provisioned Google Group '{groupName}' ({groupEmail}) for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        return resource;
    }

    // ==========================================================================
    // Gateway — add/remove per user per resource
    // ==========================================================================

    /// <inheritdoc />
    /// <remarks>
    /// GATEWAY METHOD: the only path that adds a user to a Google Group.
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

        var resource = await _resourceRepository.GetByIdAsync(groupResourceId, cancellationToken);
        if (resource is null || resource.ResourceType != GoogleResourceType.Group)
        {
            _logger.LogWarning("Group resource {ResourceId} not found", groupResourceId);
            return;
        }

        var result = await _groupMembership.CreateMembershipAsync(resource.GoogleId, userEmail, cancellationToken);

        switch (result.Outcome)
        {
            case GroupMembershipMutationOutcome.Added:
                await _auditLogService.LogGoogleSyncAsync(
                    AuditAction.GoogleResourceAccessGranted, groupResourceId,
                    $"Granted Google Group access to {userEmail} ({resource.Name})",
                    nameof(GoogleWorkspaceSyncService),
                    userEmail, "MEMBER", GoogleSyncSource.ManualSync, success: true);
                break;

            case GroupMembershipMutationOutcome.AlreadyExists:
                _logger.LogDebug("User {UserEmail} is already a member of group {GroupId}", userEmail, resource.GoogleId);
                break;

            case GroupMembershipMutationOutcome.Failed:
                await HandleGroupAddFailureAsync(groupResourceId, resource, userEmail, result.Error, cancellationToken);
                break;
        }
    }

    private async Task HandleGroupAddFailureAsync(
        Guid groupResourceId,
        GoogleResource resource,
        string userEmail,
        GoogleClientError? error,
        CancellationToken cancellationToken)
    {
        var statusCode = error?.StatusCode ?? 0;
        var errorMessage = error?.RawMessage ?? string.Empty;

        if (statusCode == 403)
        {
            var isCallerPermissionError = errorMessage.Contains("caller", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("service account", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("does not have permission", StringComparison.OrdinalIgnoreCase);

            if (isCallerPermissionError)
            {
                _logger.LogWarning(
                    "Service account lacks permission to add members to group {GroupName} ({GroupId}) — HTTP 403: {ErrorMessage}",
                    resource.Name, resource.GoogleId, errorMessage);
                return;
            }

            _logger.LogWarning(
                "Google rejected {UserEmail} for group {GroupName} ({GroupId}) — HTTP 403. " +
                "This typically means the email address does not have a Google account associated with it. " +
                "Error: {ErrorMessage}",
                userEmail, resource.Name, resource.GoogleId, errorMessage);

            // Mark the user's GoogleEmailStatus as Rejected so sync stops retrying.
            var user = await _userService.GetByEmailOrAlternateAsync(userEmail, cancellationToken);
            if (user is not null && user.GoogleEmailStatus != GoogleEmailStatus.Rejected)
            {
                await _userService.SetGoogleEmailStatusAsync(user.Id, GoogleEmailStatus.Rejected, cancellationToken);
            }

            await _auditLogService.LogGoogleSyncAsync(
                AuditAction.GoogleResourceAccessGranted, groupResourceId,
                $"Google rejected {userEmail} for group {resource.Name} — no Google account for this address (HTTP 403)",
                nameof(GoogleWorkspaceSyncService),
                userEmail, "MEMBER", GoogleSyncSource.ManualSync, success: false);
            return;
        }

        _logger.LogWarning(
            "Google API error adding {UserEmail} to group {GroupName} ({GroupId}) — HTTP {StatusCode}: {ErrorMessage}",
            userEmail, resource.Name, resource.GoogleId, statusCode, errorMessage);
    }

    /// <inheritdoc />
    /// <remarks>
    /// GATEWAY METHOD: the only path that removes a user from a Google Group.
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

        var resource = await _resourceRepository.GetByIdAsync(groupResourceId, cancellationToken);
        if (resource is null || resource.ResourceType != GoogleResourceType.Group)
        {
            _logger.LogWarning("Group resource {ResourceId} not found", groupResourceId);
            return;
        }

        var listResult = await _groupMembership.ListMembershipsAsync(resource.GoogleId, cancellationToken);
        if (listResult.Memberships is null)
        {
            _logger.LogWarning(
                "Cannot remove {UserEmail} from group {GroupId} — membership list failed (HTTP {Code}: {Message})",
                userEmail, resource.GoogleId, listResult.Error?.StatusCode, listResult.Error?.RawMessage);
            return;
        }

        var match = listResult.Memberships
            .FirstOrDefault(m => string.Equals(m.MemberEmail, userEmail, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _logger.LogDebug("User {UserEmail} not found in group {GroupId}", userEmail, resource.GoogleId);
            return;
        }

        var deleteError = await _groupMembership.DeleteMembershipAsync(match.ResourceName, cancellationToken);
        if (deleteError is not null)
        {
            _logger.LogWarning(
                "Failed to remove {UserEmail} from group {GroupId} (HTTP {Code}: {Message})",
                userEmail, resource.GoogleId, deleteError.StatusCode, deleteError.RawMessage);
            return;
        }

        await _auditLogService.LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessRevoked, groupResourceId,
            $"Removed {userEmail} from Google Group ({resource.Name})",
            nameof(GoogleWorkspaceSyncService),
            userEmail, "MEMBER", GoogleSyncSource.ManualSync, success: true);
    }

    /// <summary>
    /// GATEWAY METHOD: the only path that adds a user to a Google Drive resource.
    /// Respects SyncSettings — skips if GoogleDrive mode is None.
    /// </summary>
    /// <param name="resource">The Google resource to grant access on.</param>
    /// <param name="userEmail">The user's email address.</param>
    /// <param name="permissionLevelOverride">
    /// Optional override for the permission level. When the same Drive resource
    /// is linked to multiple teams, this is the resolved maximum level across
    /// all teams. If null, uses the resource's own DrivePermissionLevel.
    /// </param>
    private async Task AddUserToDriveAsync(
        GoogleResource resource,
        string userEmail,
        DrivePermissionLevel? permissionLevelOverride,
        CancellationToken cancellationToken)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            _logger.LogDebug("Skipping AddUserToDrive — GoogleDrive sync mode is None");
            return;
        }

        var effectiveLevel = permissionLevelOverride ?? resource.DrivePermissionLevel;
        var apiRole = effectiveLevel.ToApiRole();

        var result = await _drivePermissions.CreatePermissionAsync(resource.GoogleId, userEmail, apiRole, cancellationToken);

        switch (result.Outcome)
        {
            case DrivePermissionCreateOutcome.Created:
                await _auditLogService.LogGoogleSyncAsync(
                    AuditAction.GoogleResourceAccessGranted, resource.Id,
                    $"Granted Drive access ({effectiveLevel}) to {userEmail} ({resource.Name})",
                    nameof(GoogleWorkspaceSyncService),
                    userEmail, apiRole, GoogleSyncSource.ManualSync, success: true);
                break;

            case DrivePermissionCreateOutcome.AlreadyExists:
                _logger.LogDebug("Permission already exists for {Email} on {GoogleId}", userEmail, resource.GoogleId);
                break;

            case DrivePermissionCreateOutcome.Failed:
                _logger.LogWarning(
                    "Google API error granting {Role} to {Email} on {GoogleId} — HTTP {Code}: {Message}",
                    apiRole, userEmail, resource.GoogleId,
                    result.Error?.StatusCode, result.Error?.RawMessage);
                break;
        }
    }

    /// <summary>
    /// GATEWAY METHOD: the only path that removes a user from a Google Drive
    /// resource. Respects SyncSettings — skips if GoogleDrive mode is not
    /// AddAndRemove.
    /// </summary>
    private async Task RemoveUserFromDriveAsync(
        GoogleResource resource,
        string permissionId,
        string userEmail,
        CancellationToken cancellationToken)
    {
        var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode != SyncMode.AddAndRemove)
        {
            _logger.LogDebug("Skipping RemoveUserFromDrive — GoogleDrive sync mode is {Mode}", mode);
            return;
        }

        var error = await _drivePermissions.DeletePermissionAsync(resource.GoogleId, permissionId, cancellationToken);
        if (error is not null)
        {
            _logger.LogWarning(
                "Google API error deleting permission {PermissionId} on {GoogleId} — HTTP {Code}: {Message}",
                permissionId, resource.GoogleId, error.StatusCode, error.RawMessage);
            return;
        }

        await _auditLogService.LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessRevoked, resource.Id,
            $"Removed Drive access for {userEmail} ({resource.Name})",
            nameof(GoogleWorkspaceSyncService),
            userEmail, resource.DrivePermissionLevel.ToApiRole(), GoogleSyncSource.ManualSync, success: true);
    }

    // ==========================================================================
    // Per-team helpers
    // ==========================================================================

    /// <inheritdoc />
    public async Task SyncTeamGroupMembersAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Syncing group members for team {TeamId}", teamId);

        var resources = await _resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var groupResource = resources.FirstOrDefault(r => r.ResourceType == GoogleResourceType.Group);
        if (groupResource is null)
        {
            _logger.LogWarning("No active Google Group found for team {TeamId}", teamId);
            return;
        }

        // Active team members (skip rejected), resolved via the Teams service
        // so this service does not read team_members directly.
        var teamMembers = await _teamService.GetActiveMembersForTeamsAsync([teamId], cancellationToken);
        var teamEmails = teamMembers
            .Select(TryGetGoogleEmail)
            .OfType<string>()
            .ToList();

        // Current Google Group members via Cloud Identity.
        var currentGroupMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listResult = await _groupMembership.ListMembershipsAsync(groupResource.GoogleId, cancellationToken);
        if (listResult.Memberships is null)
        {
            var code = listResult.Error?.StatusCode ?? 0;
            if (code == 404 || code == 403)
            {
                _logger.LogWarning("Group {GroupId} not found in Google (HTTP {Code})", groupResource.GoogleId, code);
                return;
            }
            _logger.LogWarning(
                "Failed to list memberships for group {GroupId} (HTTP {Code}: {Message})",
                groupResource.GoogleId, code, listResult.Error?.RawMessage);
            return;
        }

        foreach (var m in listResult.Memberships)
        {
            if (!string.IsNullOrEmpty(m.MemberEmail))
                currentGroupMembers.Add(m.MemberEmail);
        }

        // Add missing members (removal disabled — sync is add-only until automated sync is validated).
        foreach (var email in teamEmails)
        {
            if (!currentGroupMembers.Contains(email))
            {
                await AddUserToGroupAsync(groupResource.Id, email, cancellationToken);
            }
        }

        await _resourceRepository.MarkSyncedAsync(groupResource.Id, _clock.GetCurrentInstant(), cancellationToken);

        _logger.LogInformation("Synced group members for team {TeamId}: {MemberCount} members", teamId, teamEmails.Count);
    }

    /// <inheritdoc />
    public Task<GoogleResource?> GetResourceStatusAsync(
        Guid resourceId,
        CancellationToken cancellationToken = default)
        => _resourceRepository.GetByIdAsync(resourceId, cancellationToken);

    /// <inheritdoc />
    public async Task AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetByIdAsync(userId, cancellationToken);
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

        // When GoogleEmail differs from the OAuth email, the old email should be
        // removed from Groups to prevent duplicate delivery (e.g. user got a new
        // @nobodies.team address).
        var previousEmail = !string.Equals(googleEmail, user.Email, StringComparison.OrdinalIgnoreCase)
            ? user.Email
            : null;

        var resources = await _resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);

        foreach (var resource in resources)
        {
            if (resource.ResourceType == GoogleResourceType.Group)
            {
                await AddUserToGroupAsync(resource.Id, googleEmail, cancellationToken);
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

        // Subteam member rollup: also add to parent department resources.
        var team = await _teamService.GetTeamByIdAsync(teamId, cancellationToken);
        if (team?.ParentTeamId is not null)
        {
            var parentResources = await _resourceRepository.GetActiveByTeamIdAsync(team.ParentTeamId.Value, cancellationToken);
            foreach (var resource in parentResources)
            {
                if (resource.ResourceType == GoogleResourceType.Group)
                {
                    await AddUserToGroupAsync(resource.Id, googleEmail, cancellationToken);
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
    }

    /// <inheritdoc />
    public Task RemoveUserFromTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Individual user removal is a no-op — removals are handled by the
        // reconciliation job via RemoveUserFromGroupAsync / RemoveUserFromDriveAsync.
        _logger.LogDebug(
            "Per-user removal deferred to reconciliation for user {UserId} team {TeamId}",
            userId, teamId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RestoreUserToAllTeamsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restoring Google resource access for user {UserId}", userId);

        var user = await _userService.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning("User {UserId} not found for access restoration", userId);
            return;
        }

        var memberships = await _teamService.GetUserTeamsAsync(userId, cancellationToken);
        foreach (var membership in memberships.Where(tm => tm.LeftAt is null))
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

    // ==========================================================================
    // Reconciliation — preview / execute across a resource type or single resource
    // ==========================================================================

    /// <inheritdoc />
    public async Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SyncResourcesByType: type={ResourceType}, action={Action}", resourceType, action);

        // Load every resource of this type, active-only. Cross-reference teams
        // (including soft-deleted) through the Teams service so we never touch
        // the team graph directly — the resource's Team nav is hydrated via
        // ITeamService.GetTeamByIdAsync / GetByIdsWithParentsAsync.
        IReadOnlyList<GoogleResource> resources;
        if (resourceType == GoogleResourceType.Group)
        {
            // No type-specific repo query for Groups; coalesce the per-team
            // dictionary at ~500-user scale.
            var allCounts = await _resourceRepository.GetActiveResourceCountsByTeamAsync(cancellationToken);
            var teamIds = allCounts.Keys.ToList();
            var perTeam = await _resourceRepository.GetActiveByTeamIdsAsync(teamIds, cancellationToken);
            resources = perTeam.SelectMany(kvp => kvp.Value)
                .Where(r => r.ResourceType == GoogleResourceType.Group && r.IsActive)
                .ToList();
        }
        else
        {
            var allActive = await _resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);
            resources = allActive.Where(r => r.ResourceType == resourceType && r.IsActive).ToList();
        }

        if (resources.Count == 0)
        {
            return new SyncPreviewResult { Diffs = [] };
        }

        var now = _clock.GetCurrentInstant();

        // Hydrate Team slices for every referenced team id in a single batch
        // (ITeamService.GetByIdsWithParentsAsync returns both the team and any
        // referenced parent so we can resolve department names and slugs).
        var teamIds2 = resources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await _teamService.GetByIdsWithParentsAsync(teamIds2, cancellationToken);
        foreach (var resource in resources)
        {
            if (teamsById.TryGetValue(resource.TeamId, out var team))
            {
                resource.Team = team;
            }
        }

        // Pre-load active members (primary team members) and child-team members
        // for the referenced team ids — these feed every SyncGroup/SyncDrive call.
        var primaryMembersByTeam = await LoadActiveMembersByTeamAsync(teamIds2, cancellationToken);
        var childMembersByParent = await LoadChildMembersByParentAsync(teamIds2, cancellationToken);

        List<ResourceSyncDiff> diffs;

        if (resourceType == GoogleResourceType.Group)
        {
            // Groups: one group per team — compute diffs sequentially (the bridge
            // handles its own internal retries; the old parallel-sync pattern was
            // purely to avoid DbContext thread-safety, which no longer applies).
            diffs = [];
            foreach (var resource in resources)
            {
                var members = primaryMembersByTeam.GetValueOrDefault(resource.TeamId, []);
                var childMembers = childMembersByParent.GetValueOrDefault(resource.TeamId, []);
                diffs.Add(await SyncGroupResourceAsync(resource, action, now, members, childMembers, cancellationToken));
            }
        }
        else
        {
            // Drive resources: group by GoogleId since multiple teams can share one resource.
            var grouped = resources.GroupBy(r => r.GoogleId, StringComparer.Ordinal).ToList();
            diffs = [];
            foreach (var group in grouped)
            {
                var list = group.ToList();
                var allMembers = new Dictionary<Guid, List<TeamMember>>();
                var allChildMembers = new Dictionary<Guid, List<TeamMember>>();
                foreach (var r in list)
                {
                    allMembers[r.TeamId] = primaryMembersByTeam.GetValueOrDefault(r.TeamId, []).ToList();
                    allChildMembers[r.TeamId] = childMembersByParent.GetValueOrDefault(r.TeamId, []).ToList();
                }
                diffs.Add(await SyncDriveResourceGroupAsync(list, action, now, allMembers, allChildMembers, cancellationToken));
            }
        }

        if (action == SyncAction.Execute)
        {
            // Deactivate soft-deleted-team resources now that reconciliation has
            // revoked their Google access. Only when mode was AddAndRemove.
            var serviceType = resourceType == GoogleResourceType.Group
                ? SyncServiceType.GoogleGroups
                : SyncServiceType.GoogleDrive;
            var mode = await _syncSettingsService.GetModeAsync(serviceType, cancellationToken);
            if (mode == SyncMode.AddAndRemove)
            {
                // Track errored diffs by GoogleId, not ResourceId — Drive resources
                // are grouped by GoogleId and a single diff represents potentially
                // many GoogleResource rows.
                var erroredGoogleIds = diffs
                    .Where(d => !string.IsNullOrEmpty(d.ErrorMessage))
                    .Select(d => d.GoogleId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToHashSet(StringComparer.Ordinal);

                // Only deactivate when EVERY one of a soft-deleted team's resources
                // of this type reconciled without error.
                var softDeletedTeamIds = resources
                    .Where(r => r.Team is { IsActive: false } && r.IsActive)
                    .GroupBy(r => r.TeamId)
                    .Where(g => g.All(r => !erroredGoogleIds.Contains(r.GoogleId)))
                    .Select(g => g.Key)
                    .ToList();

                if (softDeletedTeamIds.Count > 0)
                {
                    var teamResourceService = _serviceProvider.GetRequiredService<ITeamResourceService>();
                    foreach (var teamId in softDeletedTeamIds)
                    {
                        await teamResourceService.DeactivateResourcesForTeamAsync(
                            teamId, resourceType, cancellationToken);
                    }
                }
            }
        }

        return new SyncPreviewResult { Diffs = diffs };
    }

    /// <inheritdoc />
    public async Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SyncSingleResource: resourceId={ResourceId}, action={Action}", resourceId, action);

        var resource = await _resourceRepository.GetByIdAsync(resourceId, cancellationToken);
        if (resource is null)
        {
            return new ResourceSyncDiff
            {
                ResourceId = resourceId,
                ErrorMessage = "Resource not found"
            };
        }

        // Hydrate Team + any parent.
        var teamsById = await _teamService.GetByIdsWithParentsAsync([resource.TeamId], cancellationToken);
        if (teamsById.TryGetValue(resource.TeamId, out var team))
        {
            resource.Team = team;
        }

        var now = _clock.GetCurrentInstant();

        if (resource.ResourceType == GoogleResourceType.Group)
        {
            var members = await _teamService.GetActiveMembersForTeamsAsync([resource.TeamId], cancellationToken);
            var childMembers = await _teamService.GetActiveChildMembersByParentIdsAsync([resource.TeamId], cancellationToken);
            return await SyncGroupResourceAsync(resource, action, now, members, childMembers, cancellationToken);
        }

        // Drive resource: find ALL resources with same GoogleId to get full team union.
        var all = await _resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);
        var allWithSameGoogleId = all
            .Where(r => string.Equals(r.GoogleId, resource.GoogleId, StringComparison.Ordinal) && r.IsActive)
            .ToList();

        // For files (non-folder), the query above may have excluded them — merge back.
        if (allWithSameGoogleId.Count == 0 || resource.ResourceType != GoogleResourceType.DriveFolder)
        {
            allWithSameGoogleId = [resource];
        }

        var teamIds = allWithSameGoogleId.Select(r => r.TeamId).Distinct().ToList();
        var teamDict = await _teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);
        foreach (var r in allWithSameGoogleId)
        {
            if (teamDict.TryGetValue(r.TeamId, out var t)) r.Team = t;
        }

        var primary = await LoadActiveMembersByTeamAsync(teamIds, cancellationToken);
        var childBy = await LoadChildMembersByParentAsync(teamIds, cancellationToken);
        var allMembers = teamIds.ToDictionary(id => id, id => primary.GetValueOrDefault(id, []).ToList());
        var allChildMembers = teamIds.ToDictionary(id => id, id => childBy.GetValueOrDefault(id, []).ToList());

        return await SyncDriveResourceGroupAsync(allWithSameGoogleId, action, now, allMembers, allChildMembers, cancellationToken);
    }

    private async Task<ResourceSyncDiff> SyncGroupResourceAsync(
        GoogleResource resource,
        SyncAction action,
        Instant now,
        IReadOnlyList<TeamMember> teamMembers,
        IReadOnlyList<TeamMember> childMembers,
        CancellationToken cancellationToken)
    {
        try
        {
            // Expected: team's active members (skip rejected) — include subteam rollup.
            var expectedMembers = new List<ExpectedMember>();
            foreach (var tm in teamMembers)
            {
                var email = TryGetGoogleEmail(tm);
                if (email is null) continue;
                expectedMembers.Add(new ExpectedMember(email, GetDisplayName(tm), GetUserId(tm), GetProfilePictureUrl(tm)));
            }
            foreach (var cm in childMembers)
            {
                var email = TryGetGoogleEmail(cm);
                if (email is null) continue;
                if (expectedMembers.Any(m => NormalizingEmailComparer.Instance.Equals(m.Email, email)))
                    continue;
                expectedMembers.Add(new ExpectedMember(email, GetDisplayName(cm), GetUserId(cm), GetProfilePictureUrl(cm)));
            }

            var expectedEmails = new HashSet<string>(
                expectedMembers.Select(m => m.Email), NormalizingEmailComparer.Instance);

            // Current: group members via Cloud Identity.
            var saEmail = await _teamResourceClient.GetServiceAccountEmailAsync(cancellationToken);
            var listResult = await _groupMembership.ListMembershipsAsync(resource.GoogleId, cancellationToken);
            if (listResult.Memberships is null)
            {
                var code = listResult.Error?.StatusCode ?? 0;
                if (code == 404 || code == 403)
                {
                    _logger.LogWarning("Group {GroupId} not found in Google for resource {ResourceId} (HTTP {Code})",
                        resource.GoogleId, resource.Id, code);
                    const string msg = "Group not found in Google";
                    if (action == SyncAction.Execute)
                    {
                        await _resourceRepository.SetErrorMessageManyAsync([resource.Id], msg, cancellationToken);
                    }
                    return new ResourceSyncDiff
                    {
                        ResourceId = resource.Id,
                        ResourceName = resource.Name,
                        ResourceType = resource.ResourceType.ToString(),
                        GoogleId = resource.GoogleId,
                        Url = resource.Url,
                        LinkedTeams = [new TeamLink(resource.Team.Name, resource.Team.Slug)],
                        ErrorMessage = msg
                    };
                }

                _logger.LogWarning(
                    "Failed to list memberships for group {GroupId} — HTTP {Code}: {Message}",
                    resource.GoogleId, code, listResult.Error?.RawMessage);
                var errMsg = $"Google API error: {code} — {listResult.Error?.RawMessage}";
                if (action == SyncAction.Execute)
                {
                    await _resourceRepository.SetErrorMessageManyAsync([resource.Id], errMsg, cancellationToken);
                }
                return new ResourceSyncDiff
                {
                    ResourceId = resource.Id,
                    ResourceName = resource.Name,
                    ResourceType = resource.ResourceType.ToString(),
                    GoogleId = resource.GoogleId,
                    Url = resource.Url,
                    LinkedTeams = [new TeamLink(resource.Team.Name, resource.Team.Slug)],
                    ErrorMessage = errMsg
                };
            }

            var currentEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
            foreach (var m in listResult.Memberships)
            {
                if (!string.IsNullOrEmpty(m.MemberEmail))
                    currentEmails.Add(m.MemberEmail);
            }

            // Build member sync status list.
            var members = new List<MemberSyncStatus>();
            var teamLink = new TeamLink(resource.Team.Name, resource.Team.Slug);

            foreach (var expected in expectedMembers)
            {
                var state = currentEmails.Contains(expected.Email)
                    ? MemberSyncState.Correct
                    : MemberSyncState.Missing;
                members.Add(new MemberSyncStatus(
                    expected.Email, expected.DisplayName, state, [teamLink],
                    UserId: expected.UserId, ProfilePictureUrl: expected.ProfilePictureUrl));
            }

            var extraEmails = currentEmails
                .Where(e => !expectedEmails.Contains(e) &&
                    !string.Equals(e, saEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var extraIdentities = await ResolveExtraEmailIdentitiesAsync(extraEmails, cancellationToken);

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

                await _resourceRepository.MarkSyncedAsync(resource.Id, now, cancellationToken);
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
            {
                await _resourceRepository.SetErrorMessageManyAsync([resource.Id], ex.Message, cancellationToken);
            }
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
    }

    private async Task<ResourceSyncDiff> SyncDriveResourceGroupAsync(
        List<GoogleResource> resources,
        SyncAction action,
        Instant now,
        Dictionary<Guid, List<TeamMember>> membersByTeam,
        Dictionary<Guid, List<TeamMember>> childMembersByTeam,
        CancellationToken cancellationToken)
    {
        var primary = resources[0];

        // Build a lookup from team slug to permission level.
        var levelByTeamSlug = new Dictionary<string, DrivePermissionLevel>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var slug = resource.Team.Slug;
            if (!levelByTeamSlug.TryGetValue(slug, out var existing) || resource.DrivePermissionLevel > existing)
                levelByTeamSlug[slug] = resource.DrivePermissionLevel;
        }

        try
        {
            // Expected: union of all linked teams' active members + child team rollup.
            var membersByEmail = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl, List<TeamLink> TeamLinks)>(
                NormalizingEmailComparer.Instance);

            foreach (var resource in resources)
            {
                var level = resource.DrivePermissionLevel is DrivePermissionLevel.None
                    ? null : resource.DrivePermissionLevel.ToString();
                var teamLink = new TeamLink(resource.Team.Name, resource.Team.Slug, level);

                var teamMembers = membersByTeam.GetValueOrDefault(resource.TeamId, []);
                foreach (var tm in teamMembers)
                {
                    var memberEmail = TryGetGoogleEmail(tm);
                    if (memberEmail is null) continue;

                    if (membersByEmail.TryGetValue(memberEmail, out var existing))
                    {
                        if (!existing.TeamLinks.Any(tl => string.Equals(tl.Name, teamLink.Name, StringComparison.Ordinal)))
                            existing.TeamLinks.Add(teamLink);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (GetDisplayName(tm), GetUserId(tm), GetProfilePictureUrl(tm), [teamLink]);
                    }
                }

                var childMembers = childMembersByTeam.GetValueOrDefault(resource.TeamId, []);
                foreach (var cm in childMembers)
                {
                    var memberEmail = TryGetGoogleEmail(cm);
                    if (memberEmail is null) continue;

                    var childTeamLink = new TeamLink(cm.Team.Name, cm.Team.Slug, level);
                    if (membersByEmail.TryGetValue(memberEmail, out var existing2))
                    {
                        if (!existing2.TeamLinks.Any(tl => string.Equals(tl.Name, childTeamLink.Name, StringComparison.Ordinal)))
                            existing2.TeamLinks.Add(childTeamLink);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (GetDisplayName(cm), GetUserId(cm), GetProfilePictureUrl(cm), [childTeamLink]);
                    }
                }
            }

            var linkedTeams = resources.Select(r => new TeamLink(r.Team.Name, r.Team.Slug,
                    r.DrivePermissionLevel is DrivePermissionLevel.None ? null : r.DrivePermissionLevel.ToString()))
                .DistinctBy(tl => tl.Slug, StringComparer.Ordinal).ToList();

            // Current: Drive permissions.
            var permsResult = await _drivePermissions.ListPermissionsAsync(primary.GoogleId, cancellationToken);
            if (permsResult.Permissions is null)
            {
                var code = permsResult.Error?.StatusCode ?? 0;
                var msg = $"Google API error: {code} — {permsResult.Error?.RawMessage}";
                _logger.LogWarning(
                    "Failed to list permissions for {GoogleId}: {Message}", primary.GoogleId, msg);
                if (action == SyncAction.Execute)
                {
                    await _resourceRepository.SetErrorMessageManyAsync(
                        resources.Select(r => r.Id).ToList(), msg, cancellationToken);
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
                    ErrorMessage = msg
                };
            }

            var permissions = permsResult.Permissions;
            var allEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
            var directEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
            var roleByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);

            foreach (var perm in permissions)
            {
                if (IsAnyUserPermission(perm))
                {
                    allEmails.Add(perm.EmailAddress!);
                    if (!string.IsNullOrEmpty(perm.Role))
                        roleByEmail[perm.EmailAddress!] = perm.Role;
                }
                if (IsDirectManagedPermission(perm))
                    directEmails.Add(perm.EmailAddress!);
            }

            var members = new List<MemberSyncStatus>();

            foreach (var (email, (displayName, userId, profilePictureUrl, teamLinks)) in membersByEmail)
            {
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
                    state = MemberSyncState.Inherited;
                }
                else
                {
                    var currentLevel = ParseApiRole(currentRole);
                    state = currentLevel.HasValue && currentLevel.Value < memberMaxLevel
                        ? MemberSyncState.WrongRole
                        : MemberSyncState.Correct;
                }

                members.Add(new MemberSyncStatus(email, displayName, state, teamLinks, currentRole, memberExpectedRole,
                    UserId: userId, ProfilePictureUrl: profilePictureUrl));
            }

            var saEmail = await _teamResourceClient.GetServiceAccountEmailAsync(cancellationToken);
            var nonMemberEmails = allEmails
                .Where(e => !membersByEmail.ContainsKey(e) &&
                    !string.Equals(e, saEmail, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var extraIdentities = await ResolveExtraEmailIdentitiesAsync(nonMemberEmails, cancellationToken);

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

            if (action == SyncAction.Execute)
            {
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
                        var permToRemove = permissions.FirstOrDefault(p =>
                            IsDirectManagedPermission(p) &&
                            NormalizingEmailComparer.Instance.Equals(p.EmailAddress, member.Email));

                        if (permToRemove?.Id is null)
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

                await _resourceRepository.MarkSyncedManyAsync(
                    resources.Select(r => r.Id).ToList(), now, cancellationToken);
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
                await _resourceRepository.SetErrorMessageManyAsync(
                    resources.Select(r => r.Id).ToList(), ex.Message, cancellationToken);
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
    }

    // ==========================================================================
    // Group linking / reactivation
    // ==========================================================================

    /// <inheritdoc />
    public async Task<GroupLinkResult> EnsureTeamGroupAsync(
        Guid teamId,
        bool confirmReactivation = false,
        CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetTeamByIdAsync(teamId, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Team {TeamId} not found for EnsureTeamGroupAsync", teamId);
            return GroupLinkResult.Ok();
        }

        var activeResources = await _resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var existingGroup = activeResources.FirstOrDefault(r => r.ResourceType == GoogleResourceType.Group);

        // If prefix was cleared, deactivate any active group resource.
        if (team.GoogleGroupPrefix is null)
        {
            if (existingGroup is not null)
            {
                await _resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);
                _logger.LogInformation("Deactivated Group resource {ResourceId} for team {TeamId} (prefix cleared)",
                    existingGroup.Id, teamId);

                await _auditLogService.LogAsync(
                    AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                    "Deactivated Google Group resource (prefix cleared)",
                    nameof(GoogleWorkspaceSyncService),
                    relatedEntityId: teamId, relatedEntityType: nameof(Team));
            }
            else
            {
                _logger.LogDebug("Team {TeamId} has no GoogleGroupPrefix and no active group, nothing to do", teamId);
            }
            return GroupLinkResult.Ok();
        }

        var expectedUrl = $"https://groups.google.com/a/{_options.Domain}/g/{team.GoogleGroupPrefix}";

        if (existingGroup is not null &&
            string.Equals(existingGroup.Url, expectedUrl, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Team {TeamId} already has active Group resource {ResourceId} matching prefix",
                teamId, existingGroup.Id);
            return GroupLinkResult.Ok();
        }

        var email = $"{team.GoogleGroupPrefix}@{_options.Domain}";

        // Cross-team active conflict check.
        var activeConflict = await FindActiveGroupByUrlAsync(expectedUrl, cancellationToken);
        if (activeConflict is not null)
        {
            if (activeConflict.TeamId == teamId)
                return GroupLinkResult.Error("This group is already linked to this team.");

            var conflictTeam = await _teamService.GetTeamByIdAsync(activeConflict.TeamId, cancellationToken);
            var conflictName = conflictTeam?.Name ?? "another team";
            return GroupLinkResult.Error($"This group is already linked to team \"{conflictName}\".");
        }

        // Inactive-for-this-team reactivation scenario.
        var inactiveForTeam = await FindInactiveGroupForTeamByUrlAsync(teamId, expectedUrl, cancellationToken);
        if (inactiveForTeam is not null && !confirmReactivation)
        {
            return GroupLinkResult.NeedsConfirmation(
                "This group was previously linked to this team. Reactivate it?",
                inactiveForTeam.Id);
        }

        var now = _clock.GetCurrentInstant();

        if (inactiveForTeam is not null && confirmReactivation)
        {
            await _resourceRepository.ReactivateAsync(
                inactiveForTeam.Id,
                inactiveForTeam.Name,
                inactiveForTeam.Url,
                now,
                newGoogleId: null,
                newPermissionLevel: null,
                cancellationToken);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), inactiveForTeam.Id,
                "Reactivated Google Group resource for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));

            return GroupLinkResult.Ok();
        }

        // Deactivate the old resource if prefix changed.
        if (existingGroup is not null)
        {
            await _resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));
        }

        // Try to find an existing Google Group via Cloud Identity; if not, create it.
        var lookup = await _groupProvisioning.LookupGroupIdAsync(email, cancellationToken);
        if (lookup.GroupNumericId is not null)
        {
            var resource = new GoogleResource
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                ResourceType = GoogleResourceType.Group,
                GoogleId = lookup.GroupNumericId,
                Name = team.Name,
                Url = expectedUrl,
                ProvisionedAt = now,
                LastSyncedAt = now,
                IsActive = true
            };

            await _resourceRepository.AddAsync(resource, cancellationToken);

            await _auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
                $"Linked existing Google Group '{team.Name}' ({email}) for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));
        }
        else
        {
            var code = lookup.Error?.StatusCode ?? 0;
            _logger.LogInformation("Google Group '{Email}' not found (HTTP {Code}), creating for team {TeamId}",
                email, code, teamId);
            await ProvisionTeamGroupAsync(teamId, email, team.Name, cancellationToken);
        }

        return GroupLinkResult.Ok();
    }

    // ==========================================================================
    // Group settings drift
    // ==========================================================================

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

        var groupResources = await GetActiveGroupResourcesAsync(cancellationToken);

        // Filter to groups whose team is still active, and stitch Team data in.
        var teamIds = groupResources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await _teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);
        var filtered = new List<GoogleResource>();
        foreach (var r in groupResources)
        {
            if (teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            {
                r.Team = t;
                filtered.Add(r);
            }
        }

        _logger.LogInformation("Checking group settings for {Count} active Google Groups", filtered.Count);

        var reports = new List<GroupSettingsDriftReport>();
        foreach (var resource in filtered)
        {
            var groupEmail = resource.Team.GoogleGroupEmail;
            if (string.IsNullOrEmpty(groupEmail))
            {
                var prefix = resource.Url?.Split("/g/").LastOrDefault();
                groupEmail = prefix is not null ? $"{prefix}@{_options.Domain}" : null;
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

    private async Task<GroupSettingsDriftReport> CheckSingleGroupSettingsAsync(
        GoogleResource resource,
        string groupEmail,
        CancellationToken cancellationToken)
    {
        var getResult = await _groupProvisioning.GetGroupSettingsAsync(groupEmail, cancellationToken);
        if (getResult.Settings is null)
        {
            var code = getResult.Error?.StatusCode ?? 0;
            if (code == 404 || code == 403)
            {
                _logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", groupEmail, code);
                return new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupEmail = groupEmail,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}"
                };
            }
            _logger.LogWarning(
                "Error fetching settings for group '{GroupEmail}' — HTTP {Code}: {Message}",
                groupEmail, code, getResult.Error?.RawMessage);
            return new GroupSettingsDriftReport
            {
                ResourceId = resource.Id,
                GroupEmail = groupEmail,
                GroupName = resource.Name,
                Url = resource.Url,
                ErrorMessage = $"Error: {getResult.Error?.RawMessage}"
            };
        }

        var drifts = new List<GroupSettingDrift>();
        var expected = BuildExpectedSettingsDictionary();
        var actualDict = SnapshotToEnforcedDict(getResult.Settings);

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

    /// <inheritdoc />
    public async Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        // Settings remediation is always allowed — it doesn't add/remove members.
        var error = await _groupProvisioning.UpdateGroupSettingsAsync(
            groupEmail, BuildExpectedGroupSettings(), cancellationToken);

        if (error is not null)
        {
            _logger.LogError(
                "Failed to remediate settings for Google Group {GroupEmail} — HTTP {Code}: {Message}",
                groupEmail, error.StatusCode, error.RawMessage);
            throw new InvalidOperationException(
                $"Google Groups settings update failed for {groupEmail}: HTTP {error.StatusCode} — {error.RawMessage}");
        }

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceSettingsRemediated, nameof(GoogleResource), Guid.Empty,
            $"Remediated settings for Google Group '{groupEmail}'",
            nameof(GoogleWorkspaceSyncService));

        return true;
    }

    // ==========================================================================
    // Email mismatches / domain groups
    // ==========================================================================

    /// <inheritdoc />
    public async Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default)
    {
        var list = await _directory.ListDomainUsersAsync(cancellationToken);
        if (list.Users is null)
        {
            _logger.LogError(
                "Failed to check email mismatches via Admin SDK — HTTP {Code}: {Message}",
                list.Error?.StatusCode, list.Error?.RawMessage);
            return new EmailBackfillResult
            {
                ErrorMessage = list.Error?.RawMessage
            };
        }

        // Dictionary keyed by normalized email (gmail/googlemail aware).
        var googleUsersByNormalizedEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
        foreach (var u in list.Users)
        {
            if (!string.IsNullOrEmpty(u.PrimaryEmail))
                googleUsersByNormalizedEmail[u.PrimaryEmail] = u.PrimaryEmail;
        }

        // Load every user row and check.
        var dbUsers = await _userService.GetAllUsersAsync(cancellationToken);
        var mismatches = new List<EmailMismatch>();
        var totalChecked = 0;

        foreach (var dbUser in dbUsers)
        {
            if (dbUser.Email is null) continue;
            totalChecked++;

            if (!googleUsersByNormalizedEmail.TryGetValue(dbUser.Email, out var matchedGoogleEmail))
                continue;

            if (!string.Equals(dbUser.Email, matchedGoogleEmail, StringComparison.Ordinal))
            {
                mismatches.Add(new EmailMismatch
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
            totalChecked, mismatches.Count);

        return new EmailBackfillResult
        {
            Mismatches = mismatches,
            TotalUsersChecked = totalChecked
        };
    }

    /// <inheritdoc />
    public async Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        var listResult = await _directory.ListDomainGroupsAsync(cancellationToken);
        if (listResult.Groups is null)
        {
            _logger.LogError(
                "Failed to enumerate domain groups — HTTP {Code}: {Message}",
                listResult.Error?.StatusCode, listResult.Error?.RawMessage);
            return new AllGroupsResult
            {
                ErrorMessage = listResult.Error?.RawMessage
            };
        }

        var allGroups = listResult.Groups;
        _logger.LogInformation("Found {Count} Google Groups on domain {Domain}", allGroups.Count, _options.Domain);

        // Build a lookup: group prefix → linked Team (active, with GoogleGroupPrefix set).
        var teams = await _teamService.GetAllTeamsAsync(cancellationToken);
        var teamsByPrefix = teams
            .Where(t => t.GoogleGroupPrefix is not null)
            .GroupBy(t => t.GoogleGroupPrefix!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var expectedSettings = BuildExpectedSettingsDictionary();

        // Bounded concurrency for settings fetches.
        using var semaphore = new SemaphoreSlim(5);
        var tasks = allGroups
            .Where(g => !string.IsNullOrEmpty(g.Email))
            .Select(async group =>
            {
                var email = group.Email;
                var prefix = email.Split('@')[0];
                teamsByPrefix.TryGetValue(prefix, out var linkedTeam);

                string? errorMessage = null;
                var drifts = new List<GroupSettingDrift>();
                var actualSettings = new Dictionary<string, string>(StringComparer.Ordinal);

                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var getResult = await _groupProvisioning.GetGroupSettingsAsync(email, cancellationToken);
                    if (getResult.Settings is null)
                    {
                        var code = getResult.Error?.StatusCode ?? 0;
                        _logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", email, code);
                        errorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}";
                    }
                    else
                    {
                        PopulateActualSettings(actualSettings, getResult.Settings);
                        foreach (var (key, expectedValue) in expectedSettings)
                        {
                            actualSettings.TryGetValue(key, out var actualValue);
                            CompareGroupSetting(drifts, key, expectedValue, actualValue);
                        }
                    }
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
                    DisplayName = group.DisplayName ?? email,
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

        // Sort: linked groups first, then alphabetically by email.
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

    // ==========================================================================
    // Drive folder paths / inherited-access enforcement
    // ==========================================================================

    /// <inheritdoc />
    public async Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        var driveResources = await _resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);

        // Filter to resources whose team is still active.
        if (driveResources.Count == 0) return 0;
        var teamIds = driveResources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await _teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);
        var filtered = driveResources
            .Where(r => teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        if (filtered.Count == 0) return 0;

        var updatedCount = 0;
        foreach (var resource in filtered)
        {
            try
            {
                var fullPath = await ResolveDriveFolderPathAsync(resource.GoogleId, cancellationToken);
                if (fullPath is not null && !string.Equals(resource.Name, fullPath, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Drive folder path changed for resource {ResourceId}: '{OldName}' -> '{NewName}'",
                        resource.Id, resource.Name, fullPath);
                    await _resourceRepository.UpdateNameAsync(resource.Id, fullPath, cancellationToken);
                    updatedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to resolve Drive folder path for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        return updatedCount;
    }

    /// <summary>
    /// Resolves the full path of a Drive folder by walking the parent chain.
    /// </summary>
    private async Task<string?> ResolveDriveFolderPathAsync(string fileId, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var currentId = fileId;
        const int maxDepth = 20;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var fileResult = await _drivePermissions.GetFileAsync(currentId, cancellationToken);
            if (fileResult.File is null)
            {
                var code = fileResult.Error?.StatusCode ?? 0;
                if (code == 404)
                {
                    _logger.LogWarning("Drive folder {GoogleId} not found", currentId);
                }
                break;
            }

            var file = fileResult.File;

            // If the file IS the Shared Drive root, fetch the drive name.
            if (!string.IsNullOrEmpty(file.DriveId)
                && string.Equals(currentId, file.DriveId, StringComparison.Ordinal))
            {
                var driveResult = await _drivePermissions.GetSharedDriveAsync(file.DriveId, cancellationToken);
                if (driveResult.Drive is not null)
                {
                    segments.Add(driveResult.Drive.Name);
                }
                else
                {
                    segments.Add(file.Name ?? string.Empty);
                }
                break;
            }

            segments.Add(file.Name ?? string.Empty);

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
    public async Task SetInheritedPermissionsDisabledAsync(
        string googleFileId,
        bool restrict,
        CancellationToken cancellationToken = default)
    {
        var error = await _drivePermissions.SetInheritedPermissionsDisabledAsync(googleFileId, restrict, cancellationToken);
        if (error is not null)
        {
            _logger.LogWarning(
                "Failed to set inheritedPermissionsDisabled={Restrict} on {FileId} — HTTP {Code}: {Message}",
                restrict, googleFileId, error.StatusCode, error.RawMessage);
            throw new InvalidOperationException(
                $"Google Drive inheritedPermissionsDisabled update failed for {googleFileId}: HTTP {error.StatusCode} — {error.RawMessage}");
        }
    }

    /// <inheritdoc />
    public async Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default)
    {
        var driveResources = await _resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);

        if (driveResources.Count == 0) return 0;
        var teamIds = driveResources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await _teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);

        var restricted = driveResources
            .Where(r => r.RestrictInheritedAccess
                && r.ResourceType == GoogleResourceType.DriveFolder
                && teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        if (restricted.Count == 0) return 0;

        var correctedCount = 0;
        foreach (var resource in restricted)
        {
            try
            {
                var fileResult = await _drivePermissions.GetFileAsync(resource.GoogleId, cancellationToken);
                if (fileResult.File is null)
                {
                    if ((fileResult.Error?.StatusCode ?? 0) == 404)
                    {
                        _logger.LogWarning(
                            "Drive folder {GoogleId} not found (resource {ResourceId}) during inherited access check — may have been deleted",
                            resource.GoogleId, resource.Id);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Failed to fetch file {GoogleId} during inherited access check — HTTP {Code}: {Message}",
                            resource.GoogleId, fileResult.Error?.StatusCode, fileResult.Error?.RawMessage);
                    }
                    continue;
                }

                if (fileResult.File.InheritedPermissionsDisabled != true)
                {
                    _logger.LogWarning(
                        "Inherited access drift detected for resource {ResourceId} ({GoogleId}): " +
                        "inheritedPermissionsDisabled is {Actual}, expected true. Correcting.",
                        resource.Id, resource.GoogleId, fileResult.File.InheritedPermissionsDisabled);

                    await SetInheritedPermissionsDisabledAsync(resource.GoogleId, true, cancellationToken);

                    await _auditLogService.LogAsync(
                        AuditAction.GoogleResourceInheritanceDriftCorrected,
                        nameof(GoogleResource), resource.Id,
                        $"Corrected inherited access drift for Drive folder '{resource.Name}' — re-disabled inherited permissions",
                        "GoogleResourceReconciliationJob");

                    correctedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to check/enforce inherited access restriction for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        return correctedCount;
    }

    // ==========================================================================
    // Outbox failure count — routed through the dedicated repository.
    // ==========================================================================

    /// <inheritdoc />
    public Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default)
        => _googleSyncOutboxRepository.CountFailedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default)
        => _googleSyncOutboxRepository.GetRecentAsync(take, cancellationToken);

    // ==========================================================================
    // Private helpers — data loading / identity / permission helpers
    // ==========================================================================

    /// <summary>
    /// Batch-loads active members for every team id, stitches user slices,
    /// and returns a dictionary keyed by team id.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TeamMember>>> LoadActiveMembersByTeamAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<TeamMember>>(teamIds.Count);
        if (teamIds.Count == 0) return result;

        var members = await _teamService.GetActiveMembersForTeamsAsync(teamIds, ct);
        foreach (var teamId in teamIds)
            result[teamId] = [];
        foreach (var group in members.GroupBy(m => m.TeamId))
            result[group.Key] = group.ToList();
        return result;
    }

    /// <summary>
    /// Batch-loads active child-team members for the given parent team ids,
    /// stitches user slices, and returns a dictionary keyed by parent team id.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TeamMember>>> LoadChildMembersByParentAsync(
        IReadOnlyCollection<Guid> parentTeamIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<TeamMember>>(parentTeamIds.Count);
        if (parentTeamIds.Count == 0) return result;

        var childMembers = await _teamService.GetActiveChildMembersByParentIdsAsync(parentTeamIds, ct);
        foreach (var parentId in parentTeamIds)
            result[parentId] = [];
        foreach (var cm in childMembers)
        {
            if (cm.Team.ParentTeamId is { } parentId && result.ContainsKey(parentId))
            {
                var list = result[parentId] as List<TeamMember> ?? [.. result[parentId]];
                list.Add(cm);
                result[parentId] = list;
            }
        }
        return result;
    }

    /// <summary>
    /// Resolves the maximum <see cref="DrivePermissionLevel"/> for a user on a
    /// Drive resource, considering only the resources whose teams the user is
    /// an active member of.
    /// </summary>
    private async Task<DrivePermissionLevel> ResolvePermissionLevelForUserAsync(
        string googleId, Guid userId, CancellationToken cancellationToken)
    {
        // Which teams is the user an active member of?
        var userTeams = await _teamService.GetUserTeamsAsync(userId, cancellationToken);
        var userTeamIds = userTeams.Where(tm => tm.LeftAt is null).Select(tm => tm.TeamId).ToHashSet();
        if (userTeamIds.Count == 0)
            return DrivePermissionLevel.Contributor;

        // Which of those teams have an active resource with this Google id?
        var resourcesByTeam = await _resourceRepository.GetActiveByTeamIdsAsync(userTeamIds.ToList(), cancellationToken);
        var levels = resourcesByTeam.Values
            .SelectMany(rs => rs)
            .Where(r => string.Equals(r.GoogleId, googleId, StringComparison.Ordinal)
                && r.DrivePermissionLevel != DrivePermissionLevel.None)
            .Select(r => r.DrivePermissionLevel)
            .ToList();

        return levels.Count == 0 ? DrivePermissionLevel.Contributor : levels.Max();
    }

    /// <summary>
    /// Looks up an active Google Group resource by the given web URL, regardless
    /// of the owning team. Used by EnsureTeamGroup to detect cross-team conflicts.
    /// </summary>
    private async Task<GoogleResource?> FindActiveGroupByUrlAsync(string expectedUrl, CancellationToken ct)
    {
        // The repository doesn't expose URL-based lookup, so scan active groups.
        // At ~500-user scale the set is small (dozens of groups at most).
        var all = await GetActiveGroupResourcesAsync(ct);
        return all.FirstOrDefault(r => r.Url is not null &&
            string.Equals(r.Url, expectedUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Looks up an inactive Google Group resource for the given team by URL.
    /// Used by EnsureTeamGroup's reactivation path.
    /// </summary>
    private async Task<GoogleResource?> FindInactiveGroupForTeamByUrlAsync(
        Guid teamId, string expectedUrl, CancellationToken ct)
    {
        // Re-use FindInactiveGroupByCandidatesAsync as a best-effort lookup:
        // it matches by GoogleId / email, so feeding it the prefix@domain form
        // lets the email-based branch hit.
        var email = ExtractGroupEmailFromUrl(expectedUrl);
        if (email is null) return null;
        return await _resourceRepository.FindInactiveGroupByCandidatesAsync(
            teamId,
            googleNumericId: email, // empty-result on numeric id; email branch matches via ILike
            normalizedGroupEmail: email,
            ct);
    }

    private static string? ExtractGroupEmailFromUrl(string url)
    {
        // Expected URL form: https://groups.google.com/a/{domain}/g/{prefix}
        var parts = url.Split("/g/");
        if (parts.Length != 2) return null;
        var prefix = parts[1].Split('/')[0];
        // Reconstruct the email: {prefix}@{domain from URL}
        var beforeG = parts[0];
        var aIdx = beforeG.LastIndexOf("/a/", StringComparison.Ordinal);
        if (aIdx < 0) return null;
        var domain = beforeG[(aIdx + 3)..];
        return string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(domain)
            ? null : $"{prefix}@{domain}";
    }

    /// <summary>
    /// Returns every active Group resource across all teams. Used by the
    /// domain-wide drift-check and by the cross-team conflict check in
    /// EnsureTeamGroup. At our scale this is cheap.
    /// </summary>
    private async Task<IReadOnlyList<GoogleResource>> GetActiveGroupResourcesAsync(CancellationToken ct)
    {
        var allCounts = await _resourceRepository.GetActiveResourceCountsByTeamAsync(ct);
        var teamIds = allCounts.Keys.ToList();
        if (teamIds.Count == 0)
            return [];

        var perTeam = await _resourceRepository.GetActiveByTeamIdsAsync(teamIds, ct);
        return perTeam.Values
            .SelectMany(rs => rs)
            .Where(r => r.ResourceType == GoogleResourceType.Group && r.IsActive)
            .ToList();
    }

    private async Task<Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>>
        ResolveExtraEmailIdentitiesAsync(IEnumerable<string> emails, CancellationToken cancellationToken)
    {
        var emailList = emails.ToList();
        if (emailList.Count == 0)
            return new Dictionary<string, (string, Guid, string?)>(NormalizingEmailComparer.Instance);

        var matches = await _userEmailService.MatchByEmailsAsync(emailList, cancellationToken);
        var userIds = matches.Select(m => m.UserId).Distinct().ToList();
        var usersById = await _userService.GetByIdsAsync(userIds, cancellationToken);

        var result = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>(
            NormalizingEmailComparer.Instance);

        foreach (var match in matches)
        {
            if (usersById.TryGetValue(match.UserId, out var user))
            {
                result.TryAdd(match.Email, (user.DisplayName, match.UserId, user.ProfilePictureUrl));
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the Google service email (GoogleEmail ?? Email) for a team member,
    /// returning null when the user's <c>GoogleEmailStatus</c> is Rejected or
    /// when the user has no email at all. Pulls User data from the
    /// cross-domain nav that the Teams service stitches in-memory.
    /// </summary>
    private static string? TryGetGoogleEmail(TeamMember tm)
    {
#pragma warning disable CS0618 // Cross-domain User nav populated in-memory by ITeamService (§6b).
        var user = tm.User;
#pragma warning restore CS0618
        if (user is null)
            return null;
        if (user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
            return null;
        return user.GetGoogleServiceEmail();
    }

    private static string GetDisplayName(TeamMember tm)
    {
#pragma warning disable CS0618
        return tm.User?.DisplayName ?? string.Empty;
#pragma warning restore CS0618
    }

    private static Guid GetUserId(TeamMember tm) => tm.UserId;

    private static string? GetProfilePictureUrl(TeamMember tm)
    {
#pragma warning disable CS0618
        return tm.User?.ProfilePictureUrl;
#pragma warning restore CS0618
    }

    // ==========================================================================
    // Group permissions — classification helpers (pure, over the bridge's
    // shape-neutral DrivePermission DTO).
    // ==========================================================================

    private static bool IsAnyUserPermission(DrivePermission perm)
    {
        if (!string.Equals(perm.Type, "user", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(perm.EmailAddress))
            return false;
        if (perm.EmailAddress.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsDirectManagedPermission(DrivePermission perm)
    {
        if (!string.Equals(perm.Type, "user", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(perm.Role, "owner", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(perm.EmailAddress))
            return false;
        if (perm.EmailAddress.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase))
            return false;
        return !perm.IsInheritedOnly;
    }

    private static DrivePermissionLevel? ParseApiRole(string? role) => role switch
    {
        "reader" => DrivePermissionLevel.Viewer,
        "commenter" => DrivePermissionLevel.Commenter,
        "writer" => DrivePermissionLevel.Contributor,
        "fileOrganizer" => DrivePermissionLevel.ContentManager,
        "organizer" => DrivePermissionLevel.Manager,
        _ => null
    };

    // ==========================================================================
    // Group settings helpers
    // ==========================================================================

    private GroupSettingsExpected BuildExpectedGroupSettings() => new(
        WhoCanJoin: _options.Groups.WhoCanJoin,
        WhoCanViewMembership: _options.Groups.WhoCanViewMembership,
        WhoCanContactOwner: _options.Groups.WhoCanContactOwner,
        WhoCanPostMessage: _options.Groups.WhoCanPostMessage,
        WhoCanViewGroup: _options.Groups.WhoCanViewGroup,
        WhoCanModerateMembers: _options.Groups.WhoCanModerateMembers,
        AllowExternalMembers: _options.Groups.AllowExternalMembers,
        IsArchived: true,
        MembersCanPostAsTheGroup: true,
        IncludeInGlobalAddressList: true,
        AllowWebPosting: true,
        MessageModerationLevel: "MODERATE_NONE",
        SpamModerationLevel: "MODERATE",
        EnableCollaborativeInbox: true);

    private Dictionary<string, string> BuildExpectedSettingsDictionary()
    {
        var e = BuildExpectedGroupSettings();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WhoCanJoin"] = e.WhoCanJoin!,
            ["WhoCanViewMembership"] = e.WhoCanViewMembership!,
            ["WhoCanContactOwner"] = e.WhoCanContactOwner!,
            ["WhoCanPostMessage"] = e.WhoCanPostMessage!,
            ["WhoCanViewGroup"] = e.WhoCanViewGroup!,
            ["WhoCanModerateMembers"] = e.WhoCanModerateMembers!,
            ["AllowExternalMembers"] = e.AllowExternalMembers ? "true" : "false",
            ["IsArchived"] = e.IsArchived ? "true" : "false",
            ["MembersCanPostAsTheGroup"] = e.MembersCanPostAsTheGroup ? "true" : "false",
            ["IncludeInGlobalAddressList"] = e.IncludeInGlobalAddressList ? "true" : "false",
            ["AllowWebPosting"] = e.AllowWebPosting ? "true" : "false",
            ["MessageModerationLevel"] = e.MessageModerationLevel,
            ["SpamModerationLevel"] = e.SpamModerationLevel,
            ["EnableCollaborativeInbox"] = e.EnableCollaborativeInbox ? "true" : "false"
        };
    }

    /// <summary>
    /// Projects a <see cref="GroupSettingsSnapshot"/> to the enforced-settings
    /// dictionary used for drift comparison.
    /// </summary>
    private static Dictionary<string, string?> SnapshotToEnforcedDict(GroupSettingsSnapshot s) => new(StringComparer.Ordinal)
    {
        ["WhoCanJoin"] = s.WhoCanJoin,
        ["WhoCanViewMembership"] = s.WhoCanViewMembership,
        ["WhoCanContactOwner"] = s.WhoCanContactOwner,
        ["WhoCanPostMessage"] = s.WhoCanPostMessage,
        ["WhoCanViewGroup"] = s.WhoCanViewGroup,
        ["WhoCanModerateMembers"] = s.WhoCanModerateMembers,
        ["AllowExternalMembers"] = s.AllowExternalMembers,
        ["IsArchived"] = s.IsArchived,
        ["MembersCanPostAsTheGroup"] = s.MembersCanPostAsTheGroup,
        ["IncludeInGlobalAddressList"] = s.IncludeInGlobalAddressList,
        ["AllowWebPosting"] = s.AllowWebPosting,
        ["MessageModerationLevel"] = s.MessageModerationLevel,
        ["SpamModerationLevel"] = s.SpamModerationLevel,
        ["EnableCollaborativeInbox"] = s.EnableCollaborativeInbox
    };

    /// <summary>
    /// Populates the full actual-settings dictionary (enforced + deprecated)
    /// for the domain-wide "all groups" page. Mirrors the original service's
    /// <c>Add(...)</c> helper so deprecated settings stay visible to admins.
    /// </summary>
    private static void PopulateActualSettings(Dictionary<string, string> dict, GroupSettingsSnapshot s)
    {
        void Add(string key, string? val) { if (val is not null) dict[key] = val; }

        Add("WhoCanJoin", s.WhoCanJoin);
        Add("WhoCanViewMembership", s.WhoCanViewMembership);
        Add("WhoCanContactOwner", s.WhoCanContactOwner);
        Add("WhoCanPostMessage", s.WhoCanPostMessage);
        Add("WhoCanViewGroup", s.WhoCanViewGroup);
        Add("WhoCanModerateMembers", s.WhoCanModerateMembers);
        Add("WhoCanModerateContent", s.WhoCanModerateContent);
        Add("WhoCanAssistContent", s.WhoCanAssistContent);
        Add("WhoCanDiscoverGroup", s.WhoCanDiscoverGroup);
        Add("WhoCanLeaveGroup", s.WhoCanLeaveGroup);
        Add("AllowExternalMembers", s.AllowExternalMembers);
        Add("AllowWebPosting", s.AllowWebPosting);
        Add("IsArchived", s.IsArchived);
        Add("ArchiveOnly", s.ArchiveOnly);
        Add("MembersCanPostAsTheGroup", s.MembersCanPostAsTheGroup);
        Add("IncludeInGlobalAddressList", s.IncludeInGlobalAddressList);
        Add("EnableCollaborativeInbox", s.EnableCollaborativeInbox);
        Add("MessageModerationLevel", s.MessageModerationLevel);
        Add("SpamModerationLevel", s.SpamModerationLevel);
        Add("ReplyTo", s.ReplyTo);
        Add("CustomReplyTo", s.CustomReplyTo);
        Add("IncludeCustomFooter", s.IncludeCustomFooter);
        Add("CustomFooterText", s.CustomFooterText);
        Add("SendMessageDenyNotification", s.SendMessageDenyNotification);
        Add("DefaultMessageDenyNotificationText", s.DefaultMessageDenyNotificationText);
        Add("FavoriteRepliesOnTop", s.FavoriteRepliesOnTop);
        Add("DefaultSender", s.DefaultSender);
        Add("PrimaryLanguage", s.PrimaryLanguage);

        Add("WhoCanInvite", s.WhoCanInvite);
        Add("WhoCanAdd", s.WhoCanAdd);
        Add("ShowInGroupDirectory", s.ShowInGroupDirectory);
        Add("AllowGoogleCommunication", s.AllowGoogleCommunication);
        Add("WhoCanApproveMembers", s.WhoCanApproveMembers);
        Add("WhoCanBanUsers", s.WhoCanBanUsers);
        Add("WhoCanModifyMembers", s.WhoCanModifyMembers);
        Add("WhoCanApproveMessages", s.WhoCanApproveMessages);
        Add("WhoCanDeleteAnyPost", s.WhoCanDeleteAnyPost);
        Add("WhoCanDeleteTopics", s.WhoCanDeleteTopics);
        Add("WhoCanLockTopics", s.WhoCanLockTopics);
        Add("WhoCanMoveTopicsIn", s.WhoCanMoveTopicsIn);
        Add("WhoCanMoveTopicsOut", s.WhoCanMoveTopicsOut);
        Add("WhoCanPostAnnouncements", s.WhoCanPostAnnouncements);
        Add("WhoCanHideAbuse", s.WhoCanHideAbuse);
        Add("WhoCanMakeTopicsSticky", s.WhoCanMakeTopicsSticky);
        Add("WhoCanAssignTopics", s.WhoCanAssignTopics);
        Add("WhoCanUnassignTopic", s.WhoCanUnassignTopic);
        Add("WhoCanTakeTopics", s.WhoCanTakeTopics);
        Add("WhoCanMarkDuplicate", s.WhoCanMarkDuplicate);
        Add("WhoCanMarkNoResponseNeeded", s.WhoCanMarkNoResponseNeeded);
        Add("WhoCanMarkFavoriteReplyOnAnyTopic", s.WhoCanMarkFavoriteReplyOnAnyTopic);
        Add("WhoCanMarkFavoriteReplyOnOwnTopic", s.WhoCanMarkFavoriteReplyOnOwnTopic);
        Add("WhoCanUnmarkFavoriteReplyOnAnyTopic", s.WhoCanUnmarkFavoriteReplyOnAnyTopic);
        Add("WhoCanEnterFreeFormTags", s.WhoCanEnterFreeFormTags);
        Add("WhoCanModifyTagsAndCategories", s.WhoCanModifyTagsAndCategories);
        Add("WhoCanAddReferences", s.WhoCanAddReferences);
        Add("MessageDisplayFont", s.MessageDisplayFont);
        Add("MaxMessageBytes", s.MaxMessageBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    private sealed record ExpectedMember(string Email, string DisplayName, Guid UserId, string? ProfilePictureUrl);
}
