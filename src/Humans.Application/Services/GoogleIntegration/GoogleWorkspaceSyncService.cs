using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Helpers;
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
/// <see cref="IGoogleSyncService"/> impl: Workspace Drive reconciliation, Group provisioning, settings drift remediation.
/// Group membership reconciliation lives in <see cref="IGoogleGroupSync"/>.
/// </summary>
public sealed class GoogleWorkspaceSyncService(
    IGoogleGroupProvisioningClient groupProvisioning,
    IGoogleDrivePermissionsClient drivePermissions,
    IGoogleDirectoryClient directory,
    ITeamResourceGoogleClient teamResourceClient,
    IGoogleResourceRepository resourceRepository,
    IGoogleSyncOutboxRepository googleSyncOutboxRepository,
    ITeamService teamService,
    IUserService userService,
    IUserEmailService userEmailService,
    IGoogleGroupSync googleGroupSync,
    IAuditLogService auditLogService,
    ISyncSettingsService syncSettingsService,
    IGoogleRemovalNotificationService removalNotifications,
    IOptions<GoogleWorkspaceOptions> options,
    IClock clock,
    IServiceProvider serviceProvider,
    ILogger<GoogleWorkspaceSyncService> logger) : IGoogleSyncService
{
    private readonly GoogleWorkspaceOptions _options = options.Value;

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
        var existingActive = await resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var existing = existingActive.FirstOrDefault(r => r.ResourceType == GoogleResourceType.DriveFolder);
        if (existing is not null)
        {
            logger.LogInformation("Team {TeamId} already has active Drive folder {FolderId}", teamId, existing.GoogleId);
            return existing;
        }

        logger.LogInformation("Provisioning Drive folder '{FolderName}' for team {TeamId}", folderName, teamId);

        var create = await drivePermissions.CreateFolderAsync(folderName, _options.TeamFoldersParentId, cancellationToken);
        if (create.Folder is null)
        {
            var err = create.Error;
            throw new InvalidOperationException(
                $"Google Drive folder create failed (HTTP {err?.StatusCode}): {err?.RawMessage}");
        }

        var now = clock.GetCurrentInstant();
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

        await resourceRepository.AddAsync(resource, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Provisioned Drive folder '{resource.Name}' for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        return resource;
    }

    // ─── Gateway — add/remove per user per resource ───

    /// <summary>GATEWAY: only path that adds a user to a Drive resource. Skips when GoogleDrive mode is None. <paramref name="permissionLevelOverride"/>: resolved max across teams sharing the resource; null = use resource's level.</summary>
    private async Task AddUserToDriveAsync(
        GoogleResource resource,
        string userEmail,
        DrivePermissionLevel? permissionLevelOverride,
        CancellationToken cancellationToken)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            logger.LogDebug("Skipping AddUserToDrive — GoogleDrive sync mode is None");
            return;
        }

        var effectiveLevel = permissionLevelOverride ?? resource.DrivePermissionLevel;
        var apiRole = effectiveLevel.ToApiRole();

        var result = await drivePermissions.CreatePermissionAsync(resource.GoogleId, userEmail, apiRole, cancellationToken);

        switch (result.Outcome)
        {
            case DrivePermissionCreateOutcome.Created:
                await auditLogService.LogGoogleSyncAsync(
                    AuditAction.GoogleResourceAccessGranted, resource.Id,
                    $"Granted Drive access ({effectiveLevel}) to {userEmail} ({resource.Name})",
                    nameof(GoogleWorkspaceSyncService),
                    userEmail, apiRole, GoogleSyncSource.ManualSync, success: true);
                break;

            case DrivePermissionCreateOutcome.AlreadyExists:
                logger.LogDebug("Permission already exists for {Email} on {GoogleId}", userEmail, resource.GoogleId);
                break;

            case DrivePermissionCreateOutcome.Failed:
                logger.LogWarning(
                    "Google API error granting {Role} to {Email} on {GoogleId} — HTTP {Code}: {Message}",
                    apiRole, userEmail, resource.GoogleId,
                    result.Error?.StatusCode, result.Error?.RawMessage);
                await HandleDriveAddFailureAsync(resource, userEmail, result.Error, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Issue nobodies-collective/Humans#677 — when Drive's
    /// <c>permissions.create</c> returns a target-rejection (HTTP 400/403
    /// referencing "no Google account" / "SendNotificationEmail"), mark the
    /// owning user's <see cref="GoogleEmailStatus"/> as
    /// <see cref="GoogleEmailStatus.Rejected"/> so the orchestrator stops
    /// re-attempting until an admin clears the state. Mirrors the existing
    /// <c>GoogleGroupSyncService.HandleGroupAddFailureAsync</c> pattern.
    /// </summary>
    private async Task HandleDriveAddFailureAsync(
        GoogleResource resource,
        string userEmail,
        GoogleClientError? error,
        CancellationToken ct)
    {
        var statusCode = error?.StatusCode ?? 0;
        var rawMessage = error?.RawMessage ?? string.Empty;

        // Drive returns 400 when the recipient has no Google account on a
        // domain that supports notification-free sharing. 403 covers caller-
        // permission failures (not the target's problem) so we don't mark
        // those.
        if (statusCode != 400)
        {
            return;
        }

        // Drive-specific predicate only — generic phrases (sharing-policy) must not flip GoogleEmailStatus (#677).
        if (!IsDriveTargetRejection(rawMessage))
        {
            return;
        }

        var user = await userService.GetByEmailOrAlternateAsync(userEmail, ct);
        if (user is null || user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            return;
        }

        await userService.TrySetGoogleEmailStatusFromSyncAsync(
            user.Id,
            GoogleEmailStatus.Rejected,
            ct);

        logger.LogWarning(
            "Google rejected target email {Email} while granting Drive permission on {GoogleId} - HTTP 400. " +
            "User.GoogleEmailStatus marked Rejected. Google error: {ErrorMessage}",
            userEmail,
            resource.GoogleId,
            rawMessage);
    }

    /// <summary>Drive-specific no-Google-account detector. Generic phrases excluded — they overlap with sharing-policy errors (#677).</summary>
    private static bool IsDriveTargetRejection(string rawMessage)
        => rawMessage.Contains("does not have a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("no google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not associated with a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("sendnotificationemail", StringComparison.OrdinalIgnoreCase);

    /// <summary>GATEWAY: only path that removes a user from a Drive resource. Skips unless GoogleDrive mode is AddAndRemove. <paramref name="reason"/> forwarded to notifications (no suppression yet, #639).</summary>
    private async Task RemoveUserFromDriveAsync(
        GoogleResource resource,
        string permissionId,
        string userEmail,
        CancellationToken cancellationToken,
        SyncRemovalReason reason = SyncRemovalReason.Reconciliation)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode != SyncMode.AddAndRemove)
        {
            logger.LogDebug("Skipping RemoveUserFromDrive — GoogleDrive sync mode is {Mode}", mode);
            return;
        }

        var error = await drivePermissions.DeletePermissionAsync(resource.GoogleId, permissionId, cancellationToken);
        if (error is not null)
        {
            logger.LogWarning(
                "Google API error deleting permission {PermissionId} on {GoogleId} — HTTP {Code}: {Message}",
                permissionId, resource.GoogleId, error.StatusCode, error.RawMessage);
            return;
        }

        await auditLogService.LogGoogleSyncAsync(
            AuditAction.GoogleResourceAccessRevoked, resource.Id,
            $"Removed Drive access for {userEmail} ({resource.Name})",
            nameof(GoogleWorkspaceSyncService),
            userEmail, resource.DrivePermissionLevel.ToApiRole(), GoogleSyncSource.ManualSync, success: true);

        // Issue peterdrier/Humans#639 — notify only on confirmed delete.
        try
        {
            await removalNotifications.NotifyRemovalAsync(
                userEmail,
                resource.ResourceType,
                resource.Name,
                resource.Url,
                reason,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to enqueue Drive removal notification for {UserEmail} on {GoogleId}",
                userEmail, resource.GoogleId);
        }
    }

    /// <summary>Derive Group email from URL (groups.google.com/a/{domain}/g/{prefix}); null on parse fail (#639).</summary>
    private string? TryDeriveGroupEmail(GoogleResource resource)
    {
        if (resource.ResourceType != GoogleResourceType.Group)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(resource.Url))
        {
            return null;
        }

        const string marker = "/g/";
        var idx = resource.Url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        var prefix = resource.Url[(idx + marker.Length)..].TrimEnd('/');
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(_options.Domain))
        {
            return null;
        }
        return $"{prefix}@{_options.Domain}";
    }


    /// <inheritdoc />
    public async Task AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Issue #635 (§15i): read UserEmails through the owning section
        // service (design-rules §2c) instead of traversing user.UserEmails
        // cross-domain.
        var user = await userService.GetUserInfoAsync(userId, cancellationToken);

        // Merge-fold redirect (issue peterdrier/Humans#646): if the source
        // user has been folded into a target via AccountMergeService, the
        // outbox event was enqueued before the merge but is being dequeued
        // after. The team_members row was re-FK'd to the target, and the
        // source's UserEmails were also re-FK'd, so a lookup against the
        // source returns "no verified email" even though the target has one.
        // Follow the MergedToUserId chain to the terminal target — A→B→C
        // possible if B was later merged into C.
        var hops = 0;
        while (user is { MergedToUserId: { } targetUserId } && hops < 16)
        {
            logger.LogInformation(
                "Following merge-fold redirect for AddUserToTeamResources: source {SourceUserId} → target {TargetUserId} (team {TeamId})",
                userId, targetUserId, teamId);
            userId = targetUserId;
            user = await userService.GetUserInfoAsync(userId, cancellationToken);
            hops++;
        }

        if (hops >= 16 && user is { MergedToUserId: not null })
        {
            logger.LogWarning(
                "Merge-fold chain exceeded 16 hops for user {UserId} on team {TeamId}; provisioning against intermediate node",
                userId, teamId);
        }

        var userEmails = user is null
            ? (IReadOnlyList<UserEmailRowSnapshot>)[]
            : await userEmailService.GetEntitiesByUserIdAsync(userId, cancellationToken);

        var googleEmail = userEmails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault()
            ?? userEmails
                .Where(e => e.IsVerified && e.Provider != null)
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Email)
                .FirstOrDefault();
        if (googleEmail is null)
        {
            if (user is null)
            {
                logger.LogWarning(
                    "Skipped Google provisioning for {UserId} on team {TeamId}: user no longer exists (likely deleted between outbox enqueue and dequeue)",
                    userId, teamId);
            }
            else
            {
                logger.LogWarning(
                    "Skipped Google provisioning for {UserId} on team {TeamId}: user exists but has no verified email",
                    userId, teamId);
            }
            return;
        }

        if (user!.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug("Skipping AddUserToTeamResources for user {UserId} — GoogleEmailStatus is Rejected", userId);
            return;
        }

        var team = await teamService.GetTeamByIdAsync(teamId, cancellationToken);
        var resources = await resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);

        foreach (var resource in resources)
        {
            if (resource.ResourceType == GoogleResourceType.Group)
            {
                // Group membership reconciliation owned by IGoogleGroupSync; this path handles Drive only.
                await RequestGoogleGroupSyncAsync(resource, team?.GoogleGroupEmail, cancellationToken);
                continue;
            }

            var level = await ResolvePermissionLevelForUserAsync(
                resource.GoogleId, userId, cancellationToken);
            await AddUserToDriveAsync(resource, googleEmail, level, cancellationToken);
        }

        // Subteam member rollup: also add to parent department resources.
        if (team?.ParentTeamId is not null)
        {
            var parentTeam = await teamService.GetTeamByIdAsync(team.ParentTeamId.Value, cancellationToken);
            var parentResources = await resourceRepository.GetActiveByTeamIdAsync(team.ParentTeamId.Value, cancellationToken);
            foreach (var resource in parentResources)
            {
                if (resource.ResourceType == GoogleResourceType.Group)
                {
                    await RequestGoogleGroupSyncAsync(resource, parentTeam?.GoogleGroupEmail, cancellationToken);
                    continue;
                }

                var level = await ResolvePermissionLevelForUserAsync(
                    resource.GoogleId, userId, cancellationToken);
                await AddUserToDriveAsync(resource, googleEmail, level, cancellationToken);
            }
        }
    }

    private async Task RequestGoogleGroupSyncAsync(
        GoogleResource resource,
        string? teamGoogleGroupEmail,
        CancellationToken cancellationToken)
    {
        var groupKey = GoogleGroupKeyHelper.TryGetGroupKey(resource, teamGoogleGroupEmail, _options.Domain);
        if (groupKey is null)
        {
            logger.LogWarning(
                "Cannot request Google Group membership sync for resource {ResourceId}: group email could not be derived",
                resource.Id);
            return;
        }

        await googleGroupSync.RequestSyncAsync(groupKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveUserFromTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Individual user removal is a no-op. Drive removals are handled by
        // reconciliation, and Google Group membership is handled by
        // IGoogleGroupSync.
        logger.LogDebug(
            "Per-user removal deferred to reconciliation for user {UserId} team {TeamId}",
            userId, teamId);
        return Task.CompletedTask;
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
        logger.LogInformation("SyncResourcesByType: type={ResourceType}, action={Action}", resourceType, action);

        if (resourceType == GoogleResourceType.Group)
        {
            throw new InvalidOperationException(
                "Google Group membership sync is handled by IGoogleGroupSync.");
        }

        // Load every resource of this type, active-only. Cross-reference teams
        // (including soft-deleted) through the Teams service so we never touch
        // the team graph directly — the resource's Team nav is hydrated via
        // ITeamService.GetTeamByIdAsync / GetByIdsWithParentsAsync.
        var allActive = await resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);
        IReadOnlyList<GoogleResource> resources = allActive
            .Where(r => r.ResourceType == resourceType && r.IsActive)
            .ToList();

        if (resources.Count == 0)
        {
            return new SyncPreviewResult { Diffs = [] };
        }

        var now = clock.GetCurrentInstant();

        // Resolve TeamInfo read-models cross-section via ITeamService cache.
        // Downstream methods consume the dict directly — the obsolete
        // cross-section nav on GoogleResource is never assigned or read.
        var teamIds2 = resources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);

        // Pre-load active members (primary team members) and child-team members
        // for the referenced team ids. Drive reconciliation uses the full
        // team union for each linked Google file/folder.
        var primaryMembersByTeam = await LoadActiveMembersByTeamAsync(teamIds2, cancellationToken);
        var childMembersByParent = await LoadChildMembersByParentAsync(teamIds2, cancellationToken);

        // Drive resources: group by GoogleId since multiple teams can share one resource.
        var grouped = resources.GroupBy(r => r.GoogleId, StringComparer.Ordinal).ToList();
        var diffs = new List<ResourceSyncDiff>();
        foreach (var group in grouped)
        {
            var list = group.ToList();
            var allMembers = new Dictionary<Guid, List<TeamActiveMemberSnapshot>>();
            var allChildMembers = new Dictionary<Guid, List<TeamMember>>();
            foreach (var r in list)
            {
                allMembers[r.TeamId] = primaryMembersByTeam.GetValueOrDefault(r.TeamId, []).ToList();
                allChildMembers[r.TeamId] = childMembersByParent.GetValueOrDefault(r.TeamId, []).ToList();
            }
            diffs.Add(await SyncDriveResourceGroupAsync(list, teamsById, action, now, allMembers, allChildMembers, cancellationToken));
        }

        if (action == SyncAction.Execute)
        {
            // Deactivate soft-deleted-team resources now that reconciliation has
            // revoked their Google access. Only when mode was AddAndRemove.
            var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
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
                    .Where(r => teamsById.TryGetValue(r.TeamId, out var t) && !t.IsActive && r.IsActive)
                    .GroupBy(r => r.TeamId)
                    .Where(g => g.All(r => !erroredGoogleIds.Contains(r.GoogleId)))
                    .Select(g => g.Key)
                    .ToList();

                if (softDeletedTeamIds.Count > 0)
                {
                    var teamResourceService = serviceProvider.GetRequiredService<ITeamResourceService>();
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
        logger.LogInformation("SyncSingleResource: resourceId={ResourceId}, action={Action}", resourceId, action);

        var resource = await resourceRepository.GetByIdAsync(resourceId, cancellationToken);
        if (resource is null)
        {
            return new ResourceSyncDiff
            {
                ResourceId = resourceId,
                ErrorMessage = "Resource not found"
            };
        }

        // Resolve TeamInfo for this resource's team (cross-section via cache).
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var teamGoogleGroupEmail = teamsById.TryGetValue(resource.TeamId, out var teamInfo)
            ? teamInfo.GoogleGroupEmail
            : null;

        var now = clock.GetCurrentInstant();

        if (resource.ResourceType == GoogleResourceType.Group)
            return await ReconcileGroupResourceAsync(resource, teamGoogleGroupEmail, action, cancellationToken);

        // Drive resource: find ALL resources with same GoogleId to get full team union.
        var all = await resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);
        var allWithSameGoogleId = all
            .Where(r => string.Equals(r.GoogleId, resource.GoogleId, StringComparison.Ordinal) && r.IsActive)
            .ToList();

        // For files (non-folder), the query above may have excluded them — merge back.
        if (allWithSameGoogleId.Count == 0 || resource.ResourceType != GoogleResourceType.DriveFolder)
        {
            allWithSameGoogleId = [resource];
        }

        var teamIds = allWithSameGoogleId.Select(r => r.TeamId).Distinct().ToList();

        var primary = await LoadActiveMembersByTeamAsync(teamIds, cancellationToken);
        var childBy = await LoadChildMembersByParentAsync(teamIds, cancellationToken);
        var allMembers = teamIds.ToDictionary(id => id, id => primary.GetValueOrDefault(id, []).ToList());
        var allChildMembers = teamIds.ToDictionary(id => id, id => childBy.GetValueOrDefault(id, []).ToList());

        return await SyncDriveResourceGroupAsync(allWithSameGoogleId, teamsById, action, now, allMembers, allChildMembers, cancellationToken);
    }

    private async Task<ResourceSyncDiff> ReconcileGroupResourceAsync(
        GoogleResource resource,
        string? teamGoogleGroupEmail,
        SyncAction action,
        CancellationToken cancellationToken)
    {
        var groupKey = GoogleGroupKeyHelper.TryGetGroupKey(
            resource,
            teamGoogleGroupEmail,
            _options.Domain);

        return groupKey is null
            ? new ResourceSyncDiff
            {
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType.ToString(),
                GoogleId = resource.GoogleId,
                Url = resource.Url,
                ErrorMessage = "Cannot determine Google group email for this resource"
            }
            : await googleGroupSync.ReconcileOneAsync(groupKey, action, cancellationToken);
    }

    private async Task<ResourceSyncDiff> SyncDriveResourceGroupAsync(
        List<GoogleResource> resources,
        IReadOnlyDictionary<Guid, TeamInfo> teamsById,
        SyncAction action,
        Instant now,
        Dictionary<Guid, List<TeamActiveMemberSnapshot>> membersByTeam,
        Dictionary<Guid, List<TeamMember>> childMembersByTeam,
        CancellationToken cancellationToken)
    {
        var primary = resources[0];

        // Build a lookup from team slug to permission level.
        var levelByTeamSlug = new Dictionary<string, DrivePermissionLevel>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var slug = teamsById[resource.TeamId].Slug;
            if (!levelByTeamSlug.TryGetValue(slug, out var existing) || resource.DrivePermissionLevel > existing)
                levelByTeamSlug[slug] = resource.DrivePermissionLevel;
        }

        try
        {
            // Expected: union of all linked teams' active members + child team rollup.
            var membersByEmail = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl, List<TeamLink> TeamLinks)>(
                NormalizingEmailComparer.Instance);

            // Issue #635 (§15i): bulk-fetch UserEmails once for all members
            // across the team union so TryGetGoogleEmail does not traverse
            // user.UserEmails cross-domain.
            var allMemberUserIds = membersByTeam.Values.SelectMany(v => v.Select(m => m.UserId))
                .Concat(childMembersByTeam.Values.SelectMany(v => v.Select(m => m.UserId)))
                .Distinct()
                .ToList();
            var emailsByUserId = await LoadEmailsForUserIdsAsync(allMemberUserIds, cancellationToken);
            var usersById = await userService.GetUserInfosAsync(allMemberUserIds, cancellationToken);

            foreach (var resource in resources)
            {
                var level = resource.DrivePermissionLevel is DrivePermissionLevel.None
                    ? null : resource.DrivePermissionLevel.ToString();
                var resourceTeam = teamsById[resource.TeamId];
                var teamLink = new TeamLink(resourceTeam.Name, resourceTeam.Slug, level);

                var teamMembers = membersByTeam.GetValueOrDefault(resource.TeamId, []);
                foreach (var tm in teamMembers)
                {
                    var memberEmail = TryGetGoogleEmail(tm, emailsByUserId);
                    if (memberEmail is null) continue;

                    if (membersByEmail.TryGetValue(memberEmail, out var existing))
                    {
                        if (!existing.TeamLinks.Any(tl => string.Equals(tl.Name, teamLink.Name, StringComparison.Ordinal)))
                            existing.TeamLinks.Add(teamLink);
                    }
                    else
                    {
                        membersByEmail[memberEmail] = (tm.DisplayName, tm.UserId, tm.ProfilePictureUrl, [teamLink]);
                    }
                }

                var childMembers = childMembersByTeam.GetValueOrDefault(resource.TeamId, []);
                foreach (var cm in childMembers)
                {
                    var memberEmail = TryGetGoogleEmail(cm, emailsByUserId);
                    if (memberEmail is null) continue;

                    // cm is a TeamMember (not obsolete); text-based ratchet false-positives on the property name.
#pragma warning disable CS0618
                    var childTeamLink = new TeamLink(cm.Team.Name, cm.Team.Slug, level);
#pragma warning restore CS0618
                    if (membersByEmail.TryGetValue(memberEmail, out var existing2))
                    {
                        if (!existing2.TeamLinks.Any(tl => string.Equals(tl.Name, childTeamLink.Name, StringComparison.Ordinal)))
                            existing2.TeamLinks.Add(childTeamLink);
                    }
                    else
                    {
                        usersById.TryGetValue(cm.UserId, out var userInfo);
                        membersByEmail[memberEmail] = (
                            userInfo?.BurnerName ?? string.Empty,
                            cm.UserId,
                            userInfo?.ProfilePictureUrl,
                            [childTeamLink]);
                    }
                }
            }

            var linkedTeams = resources.Select(r =>
                {
                    var t = teamsById[r.TeamId];
                    return new TeamLink(t.Name, t.Slug,
                        r.DrivePermissionLevel is DrivePermissionLevel.None ? null : r.DrivePermissionLevel.ToString());
                })
                .DistinctBy(tl => tl.Slug, StringComparer.Ordinal).ToList();

            // Current: Drive permissions.
            var permsResult = await drivePermissions.ListPermissionsAsync(primary.GoogleId, cancellationToken);
            if (permsResult.Permissions is null)
            {
                var code = permsResult.Error?.StatusCode ?? 0;
                var msg = $"Google API error: {code} — {permsResult.Error?.RawMessage}";
                logger.LogWarning(
                    "Failed to list permissions for {GoogleId}: {Message}", primary.GoogleId, msg);
                if (action == SyncAction.Execute)
                {
                    await resourceRepository.SetErrorMessageManyAsync(
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

            var saEmail = await teamResourceClient.GetServiceAccountEmailAsync(cancellationToken);
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
                        logger.LogError(ex, "Failed to grant Drive access to {Email} on {GoogleId}",
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
                            logger.LogInformation(
                                "Skipping removal of {Email} from {GoogleId} — permission is inherited, not direct",
                                member.Email, primary.GoogleId);
                            continue;
                        }

                        await RemoveUserFromDriveAsync(primary, permToRemove.Id, member.Email, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to remove Drive access for {Email} on {GoogleId}",
                            member.Email, primary.GoogleId);
                    }
                }

                await resourceRepository.MarkSyncedManyAsync(
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
            logger.LogError(ex, "Error syncing Drive resource group {GoogleId}", primary.GoogleId);
            if (action == SyncAction.Execute)
            {
                await resourceRepository.SetErrorMessageManyAsync(
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
                LinkedTeams = resources.Select(r =>
                    {
                        var t = teamsById[r.TeamId];
                        return new TeamLink(t.Name, t.Slug,
                            r.DrivePermissionLevel is DrivePermissionLevel.None ? null : r.DrivePermissionLevel.ToString());
                    })
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
        var team = await teamService.GetTeamByIdAsync(teamId, cancellationToken);
        if (team is null)
        {
            logger.LogWarning("Team {TeamId} not found for EnsureTeamGroupAsync", teamId);
            return GroupLinkResult.Ok();
        }

        var activeResources = await resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var existingGroup = activeResources.FirstOrDefault(r => r.ResourceType == GoogleResourceType.Group);

        // If prefix was cleared, deactivate any active group resource.
        if (team.GoogleGroupPrefix is null)
        {
            if (existingGroup is not null)
            {
                await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);
                logger.LogInformation("Deactivated Group resource {ResourceId} for team {TeamId} (prefix cleared)",
                    existingGroup.Id, teamId);

                await auditLogService.LogAsync(
                    AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                    "Deactivated Google Group resource (prefix cleared)",
                    nameof(GoogleWorkspaceSyncService),
                    relatedEntityId: teamId, relatedEntityType: nameof(Team));
            }
            else
            {
                logger.LogDebug("Team {TeamId} has no GoogleGroupPrefix and no active group, nothing to do", teamId);
            }
            return GroupLinkResult.Ok();
        }

        var expectedUrl = $"https://groups.google.com/a/{_options.Domain}/g/{team.GoogleGroupPrefix}";

        if (existingGroup is not null &&
            string.Equals(existingGroup.Url, expectedUrl, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Team {TeamId} already has active Group resource {ResourceId} matching prefix",
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

            var conflictTeam = await teamService.GetTeamByIdAsync(activeConflict.TeamId, cancellationToken);
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

        var now = clock.GetCurrentInstant();

        if (inactiveForTeam is not null && confirmReactivation)
        {
            await resourceRepository.ReactivateAsync(
                inactiveForTeam.Id,
                inactiveForTeam.Name,
                inactiveForTeam.Url,
                now,
                newGoogleId: null,
                newPermissionLevel: null,
                cancellationToken);

            await auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), inactiveForTeam.Id,
                "Reactivated Google Group resource for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));

            return GroupLinkResult.Ok();
        }

        // Deactivate the old resource if prefix changed.
        if (existingGroup is not null)
        {
            await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);

            await auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: nameof(Team));
        }

        // Delegate to the central recon path. ReconcileOneAsync looks up the
        // group, auto-provisions it (with enforced settings) when missing, and
        // syncs membership. Returns a diff carrying the Google numeric id on
        // success — we use it to write the team-side GoogleResource row.
        var diff = await googleGroupSync.ReconcileOneAsync(email, SyncAction.Execute, cancellationToken, scheduleRetries: false);
        if (string.IsNullOrEmpty(diff.GoogleId))
        {
            logger.LogWarning(
                "Failed to ensure Google Group '{Email}' for team {TeamId} via recon: {Error}",
                email, teamId, diff.ErrorMessage);
            return GroupLinkResult.Error(diff.ErrorMessage ?? $"Failed to ensure Google Group {email}");
        }

        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = diff.GoogleId,
            Name = team.Name,
            Url = expectedUrl,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        await resourceRepository.AddAsync(resource, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Linked Google Group '{team.Name}' ({email}) for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        return GroupLinkResult.Ok();
    }

    // ==========================================================================
    // Group settings drift
    // ==========================================================================

    /// <inheritdoc />
    public async Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
        if (mode == SyncMode.None)
        {
            logger.LogInformation("Google Groups sync is disabled — skipping settings drift check");
            return new GroupSettingsDriftResult
            {
                Skipped = true,
                SkipReason = "Google Groups sync mode is set to None"
            };
        }

        var groupResources = await GetActiveGroupResourcesAsync(cancellationToken);

        // Filter to groups whose team is still active. TeamInfo cache provides
        // the read-model (Name/Slug/GoogleGroupPrefix/IsActive) cross-section
        // so we never traverse the obsolete cross-section nav on GoogleResource.
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var filtered = groupResources
            .Where(r => teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        logger.LogInformation("Checking group settings for {Count} active Google Groups", filtered.Count);

        var reports = new List<GroupSettingsDriftReport>();
        foreach (var resource in filtered)
        {
            var groupEmail = teamsById[resource.TeamId].GoogleGroupEmail;
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
        var getResult = await groupProvisioning.GetGroupSettingsAsync(groupEmail, cancellationToken);
        if (getResult.Settings is null)
        {
            var code = getResult.Error?.StatusCode ?? 0;
            if (code == 404 || code == 403)
            {
                logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", groupEmail, code);
                return new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupEmail = groupEmail,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}"
                };
            }
            logger.LogWarning(
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
            logger.LogWarning("Group '{GroupEmail}' has {DriftCount} setting drift(s): {Drifts}",
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
    public async Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            // Settings remediation is always allowed — it doesn't add/remove members.
            var error = await groupProvisioning.UpdateGroupSettingsAsync(
                groupEmail, BuildExpectedGroupSettings(), cancellationToken);

            if (error is not null)
            {
                logger.LogError(
                    "Failed to remediate settings for Google Group {GroupEmail} — HTTP {Code}: {Message}",
                    groupEmail, error.StatusCode, error.RawMessage);
                return GroupSettingsRemediationResult.Failure(
                    $"Google Groups settings update failed for {groupEmail}: HTTP {error.StatusCode} — {error.RawMessage}");
            }

            await auditLogService.LogAsync(
                AuditAction.GoogleResourceSettingsRemediated, nameof(GoogleResource), Guid.Empty,
                $"Remediated settings for Google Group '{groupEmail}'",
                nameof(GoogleWorkspaceSyncService));

            return GroupSettingsRemediationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remediate settings for {GroupEmail}", groupEmail);
            return GroupSettingsRemediationResult.Failure($"Remediation failed for {groupEmail}: {ex.Message}");
        }
    }

    // ==========================================================================
    // Email mismatches / domain groups
    // ==========================================================================

    /// <inheritdoc />
    public async Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        var listResult = await directory.ListDomainGroupsAsync(cancellationToken);
        if (listResult.Groups is null)
        {
            logger.LogError(
                "Failed to enumerate domain groups — HTTP {Code}: {Message}",
                listResult.Error?.StatusCode, listResult.Error?.RawMessage);
            return new AllGroupsResult
            {
                ErrorMessage = listResult.Error?.RawMessage
            };
        }

        var allGroups = listResult.Groups;
        logger.LogInformation("Found {Count} Google Groups on domain {Domain}", allGroups.Count, _options.Domain);

        // Build a lookup: group prefix → linked Team (active, with GoogleGroupPrefix set).
        var teams = await teamService.GetAllTeamsAsync(cancellationToken);
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
                    var getResult = await groupProvisioning.GetGroupSettingsAsync(email, cancellationToken);
                    if (getResult.Settings is null)
                    {
                        var code = getResult.Error?.StatusCode ?? 0;
                        logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", email, code);
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
                    logger.LogError(ex, "Error fetching settings for group '{GroupEmail}'", email);
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
        var driveResources = await resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);

        // Filter to resources whose team is still active.
        if (driveResources.Count == 0) return 0;
        var teamIds = driveResources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);
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
                    logger.LogInformation(
                        "Drive folder path changed for resource {ResourceId}: '{OldName}' -> '{NewName}'",
                        resource.Id, resource.Name, fullPath);
                    await resourceRepository.UpdateNameAsync(resource.Id, fullPath, cancellationToken);
                    updatedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
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
            var fileResult = await drivePermissions.GetFileAsync(currentId, cancellationToken);
            if (fileResult.File is null)
            {
                var code = fileResult.Error?.StatusCode ?? 0;
                if (code == 404)
                {
                    logger.LogWarning("Drive folder {GoogleId} not found", currentId);
                }
                break;
            }

            var file = fileResult.File;

            // If the file IS the Shared Drive root, fetch the drive name.
            if (!string.IsNullOrEmpty(file.DriveId)
                && string.Equals(currentId, file.DriveId, StringComparison.Ordinal))
            {
                var driveResult = await drivePermissions.GetSharedDriveAsync(file.DriveId, cancellationToken);
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
        var error = await drivePermissions.SetInheritedPermissionsDisabledAsync(googleFileId, restrict, cancellationToken);
        if (error is not null)
        {
            logger.LogWarning(
                "Failed to set inheritedPermissionsDisabled={Restrict} on {FileId} — HTTP {Code}: {Message}",
                restrict, googleFileId, error.StatusCode, error.RawMessage);
            throw new InvalidOperationException(
                $"Google Drive inheritedPermissionsDisabled update failed for {googleFileId}: HTTP {error.StatusCode} — {error.RawMessage}");
        }
    }

    /// <inheritdoc />
    public async Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default)
    {
        var driveResources = await resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);

        if (driveResources.Count == 0) return 0;
        var teamIds = driveResources.Select(r => r.TeamId).Distinct().ToList();
        var teamsById = await teamService.GetByIdsWithParentsAsync(teamIds, cancellationToken);

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
                var fileResult = await drivePermissions.GetFileAsync(resource.GoogleId, cancellationToken);
                if (fileResult.File is null)
                {
                    if ((fileResult.Error?.StatusCode ?? 0) == 404)
                    {
                        logger.LogWarning(
                            "Drive folder {GoogleId} not found (resource {ResourceId}) during inherited access check — may have been deleted",
                            resource.GoogleId, resource.Id);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Failed to fetch file {GoogleId} during inherited access check — HTTP {Code}: {Message}",
                            resource.GoogleId, fileResult.Error?.StatusCode, fileResult.Error?.RawMessage);
                    }
                    continue;
                }

                if (fileResult.File.InheritedPermissionsDisabled != true)
                {
                    logger.LogWarning(
                        "Inherited access drift detected for resource {ResourceId} ({GoogleId}): " +
                        "inheritedPermissionsDisabled is {Actual}, expected true. Correcting.",
                        resource.Id, resource.GoogleId, fileResult.File.InheritedPermissionsDisabled);

                    await SetInheritedPermissionsDisabledAsync(resource.GoogleId, true, cancellationToken);

                    await auditLogService.LogAsync(
                        AuditAction.GoogleResourceInheritanceDriftCorrected,
                        nameof(GoogleResource), resource.Id,
                        $"Corrected inherited access drift for Drive folder '{resource.Name}' — re-disabled inherited permissions",
                        "GoogleResourceReconciliationJob");

                    correctedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
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
        => googleSyncOutboxRepository.CountFailedAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var events = await googleSyncOutboxRepository.GetRecentAsync(take, cancellationToken);
        return events
            .Select(e => new GoogleSyncOutboxEventSnapshot(
                e.EventType,
                e.TeamId,
                e.UserId,
                e.OccurredAt,
                e.ProcessedAt,
                e.RetryCount,
                e.LastError,
                e.FailedPermanently))
            .ToList();
    }

    // ==========================================================================
    // Private helpers — data loading / identity / permission helpers
    // ==========================================================================

    /// <summary>
    /// Batch-loads active members for every team id, stitches user slices,
    /// and returns a dictionary keyed by team id.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TeamActiveMemberSnapshot>>> LoadActiveMembersByTeamAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<TeamActiveMemberSnapshot>>(teamIds.Count);
        if (teamIds.Count == 0) return result;

        var teamsById = await teamService.GetTeamsAsync(ct);
        foreach (var teamId in teamIds)
        {
            if (teamsById.TryGetValue(teamId, out var team))
            {
                result[teamId] = team.Members
                    .Select(m => new TeamActiveMemberSnapshot(
                        teamId, m.TeamMemberId, m.UserId,
                        m.DisplayName, m.Email, m.ProfilePictureUrl,
                        m.GoogleEmailStatus, m.Role, m.JoinedAt))
                    .ToList();
            }
            else
            {
                result[teamId] = [];
            }
        }
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

        var parentSet = parentTeamIds.ToHashSet();
        var allTeams = await teamService.GetAllTeamsAsync(ct);
        var childMembersByParentId = parentTeamIds.ToDictionary(
            parentId => parentId,
            _ => new List<TeamMember>());
        foreach (var team in allTeams.Where(t => t.ParentTeamId is { } parentId && parentSet.Contains(parentId)))
        {
            foreach (var member in team.Members.Where(m => m.LeftAt is null))
            {
                // member is a TeamMember (not obsolete); text-based ratchet false-positives on the property name.
#pragma warning disable CS0618
                member.Team = team;
#pragma warning restore CS0618
                childMembersByParentId[team.ParentTeamId!.Value].Add(member);
            }
        }

        var childMembers = childMembersByParentId.Values.SelectMany(m => m).ToList();
        await StitchUserSlicesAsync(childMembers, ct);

        foreach (var parentId in parentTeamIds)
            result[parentId] = childMembersByParentId.GetValueOrDefault(parentId) ?? [];
        return result;
    }

    private async Task StitchUserSlicesAsync(IReadOnlyList<TeamMember> members, CancellationToken ct)
    {
        if (members.Count == 0)
            return;

        var users = await userService.GetByIdsAsync(
            members.Select(m => m.UserId).Distinct().ToList(),
            ct);

        foreach (var member in members)
        {
            if (users.TryGetValue(member.UserId, out var user))
            {
#pragma warning disable CS0618
                member.User = user;
#pragma warning restore CS0618
            }
        }
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
        var userTeams = await teamService.GetUserTeamsAsync(userId, cancellationToken);
        var userTeamIds = userTeams.Where(tm => tm.LeftAt is null).Select(tm => tm.TeamId).ToHashSet();
        if (userTeamIds.Count == 0)
            return DrivePermissionLevel.Contributor;

        // Which of those teams have an active resource with this Google id?
        var resourcesByTeam = await resourceRepository.GetActiveByTeamIdsAsync(userTeamIds.ToList(), cancellationToken);
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
        return await resourceRepository.FindInactiveGroupByCandidatesAsync(
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
        var allCounts = await resourceRepository.GetActiveResourceCountsByTeamAsync(ct);
        var teamIds = allCounts.Keys.ToList();
        if (teamIds.Count == 0)
            return [];

        var perTeam = await resourceRepository.GetActiveByTeamIdsAsync(teamIds, ct);
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

        var matches = await userEmailService.MatchByEmailsAsync(emailList, cancellationToken);
        var userIds = matches.Select(m => m.UserId).Distinct().ToList();
        var usersById = await userService.GetUserInfosAsync(userIds, cancellationToken);

        var result = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>(
            NormalizingEmailComparer.Instance);

        foreach (var match in matches)
        {
            if (usersById.TryGetValue(match.UserId, out var user))
            {
                result.TryAdd(match.Email, (user.BurnerName, match.UserId, user.ProfilePictureUrl));
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the canonical Google Workspace email for a team member, returning
    /// null when the user's <c>GoogleEmailStatus</c> is Rejected or when the
    /// user has no Workspace identity at all. Issue #635 (§15i): UserEmails
    /// are pre-fetched by the caller via <see cref="IUserEmailService.GetEntitiesByUserIdsAsync"/>
    /// and passed in as <paramref name="emailsByUserId"/> instead of being
    /// traversed through <c>tm.User.UserEmails</c>.
    /// </summary>
    private static string? TryGetGoogleEmail(
        TeamMember tm,
        IReadOnlyDictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>> emailsByUserId)
    {
#pragma warning disable CS0618 // Cross-domain User nav populated in-memory by ITeamService (§6b).
        var user = tm.User;
#pragma warning restore CS0618
        // §6b stitcher can miss: User nav is annotated non-null but populated in-memory
        // by ITeamService — if the stitch didn't include this row, user is null at runtime.
        if (user is null)
            return null;
        if (user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
            return null;

        var emails = emailsByUserId.TryGetValue(tm.UserId, out var list)
            ? list
            : [];

        return emails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault()
            ?? emails
                .Where(e => e.IsVerified && e.Provider != null)
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Email)
                .FirstOrDefault();
    }

    /// <summary>Bulk-fetch UserEmails by userId for sync hot-path (#635, §15i).</summary>
    private Task<IReadOnlyDictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>>
        LoadEmailsForUserIdsAsync(
            IReadOnlyCollection<Guid> userIds,
            CancellationToken ct)
    {
        return userEmailService.GetEntitiesByUserIdsAsync(userIds, ct);
    }

    private static string? TryGetGoogleEmail(
        TeamActiveMemberSnapshot tm,
        IReadOnlyDictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>> emailsByUserId)
    {
        if (tm.GoogleEmailStatus == GoogleEmailStatus.Rejected)
            return null;

        var emails = emailsByUserId.TryGetValue(tm.UserId, out var list)
            ? list
            : [];

        return emails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault();
    }

    // ─── Group permission classifiers over DrivePermission DTO ───

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

    private GroupSettingsExpected BuildExpectedGroupSettings() =>
        GroupSettingsPolicy.BuildExpected(_options.Groups);

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
    /// for the domain-wide "all groups" page — deprecated settings stay visible
    /// to admins.
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
