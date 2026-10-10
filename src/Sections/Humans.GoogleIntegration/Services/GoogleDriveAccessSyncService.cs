using Humans.Base.Attributes;
using Humans.GoogleIntegration.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>Reconciles Drive resources claimed by Teams and Workgroups through their public sources.</summary>
[CrossSectionWrite("Drive sync records rejected Google addresses through Users.")]
internal sealed class GoogleDriveAccessSyncService(
    IEnumerable<IGoogleDriveAccessSource> sources,
    IGoogleDrivePermissionsClient drivePermissions,
    IUserService userService,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleRemovalNotificationService removalNotifications,
    IGoogleResourceRepository resourceRepository,
    ITeamServiceRead teamService,
    ITeamResourceService teamResourceService,
    IClock clock,
    ILogger<GoogleDriveAccessSyncService> logger) : IGoogleDriveSync
{
    public async Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleResourceType? resourceType = null,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var resources = await LoadResourcesAsync(ct);
        var claims = await LoadClaimsAsync(sources, folderId: null, logger, ct);
        claims = claims.Select(claim => claim with
        {
            Resources = resources.Where(r => string.Equals(r.GoogleId, claim.FolderId, StringComparison.Ordinal)).ToList()
        }).Where(claim => resourceType is null || (claim.Resources.Count == 0
            ? resourceType == GoogleResourceType.DriveFolder
            : claim.Resources.Any(r => r.ResourceType == resourceType)))
            .Select(claim => claim with
            {
                Resources = claim.Resources.OrderBy(r => resourceType is not null && r.ResourceType != resourceType).ToList()
            }).ToList();
        var diffs = new List<ResourceSyncDiff>();

        foreach (var claim in claims)
        {
            diffs.Add(await ReconcileClaimSafelyAsync(claim, action, syncSource, ct));
        }

        if (action == SyncAction.Execute
            && await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct) == SyncMode.AddAndRemove)
            await DeactivateRetiredTeamResourcesAsync(
                resources.Where(r => resourceType is null || r.ResourceType == resourceType).ToList(), diffs, ct);
        return new SyncPreviewResult { Diffs = diffs };
    }

    public async Task<ResourceSyncDiff> ReconcileOneAsync(
        string folderId,
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var resources = await LoadResourcesAsync(ct);
        var claims = await LoadClaimsAsync(sources, folderId, logger, ct);
        var claim = claims.SingleOrDefault(c =>
            string.Equals(c.FolderId, folderId, StringComparison.OrdinalIgnoreCase));

        if (claim is null)
        {
            const string error = "No Google Drive access source claims this folder";
            logger.LogWarning("Google Drive access sync: no source claims folder id {FolderId}", folderId);
            return BuildDiff(new FolderClaim(folderId, 0, [], new([]),
                resources.Where(r => string.Equals(r.GoogleId, folderId, StringComparison.Ordinal)).ToList()), [], error);
        }

        claim = claim with
        {
            Resources = resources.Where(r => string.Equals(r.GoogleId, folderId, StringComparison.Ordinal)).ToList()
        };
        return await ReconcileClaimSafelyAsync(claim, action, syncSource, ct);
    }

    /// <summary>
    /// Loads every source's claims, one <see cref="IGoogleDriveAccessSource"/>
    /// at a time so a throwing source is skipped without aborting the others.
    /// </summary>
    internal static async Task<IReadOnlyList<FolderClaim>> LoadClaimsAsync(
        IEnumerable<IGoogleDriveAccessSource> sources, string? folderId, ILogger logger, CancellationToken ct)
    {
        var claims = new List<(string SourceName, string FolderId, GoogleDriveAccessClaim Claim)>();

        foreach (var source in sources)
        {
            Dictionary<string, GoogleDriveAccessClaim> expected;
            try
            {
                expected = await source.GetExpectedAccessAsync(folderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Google Drive access source {SourceType} threw while computing expected access; skipping this source",
                    source.GetType().Name);
                continue;
            }

            foreach (var (key, access) in expected)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                claims.Add((source.GetType().Name, key.Trim(), access));
            }
        }

        return claims
            .GroupBy(c => c.FolderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FolderClaim(
                g.Key,
                g.Count(),
                g.Select(c => c.SourceName).Distinct(StringComparer.Ordinal).ToArray(),
                g.First().Claim, []))
            .ToList();
    }

    private async Task<IReadOnlyList<GoogleResource>> LoadResourcesAsync(CancellationToken ct)
    {
        var resources = new List<GoogleResource>();
        foreach (var type in new[] { GoogleResourceType.DriveFolder, GoogleResourceType.DriveFile, GoogleResourceType.SharedDrive })
            resources.AddRange(await resourceRepository.GetActiveByResourceTypeAsync(type, ct));
        return resources;
    }

    private async Task<ResourceSyncDiff> ReconcileClaimSafelyAsync(
        FolderClaim claim, SyncAction action, GoogleSyncSource syncSource, CancellationToken ct)
    {
        ResourceSyncDiff diff;
        try
        {
            diff = claim.IsCollision
                ? await BuildCollisionDiffAsync(claim)
                : await ReconcileClaimAsync(claim, action, syncSource, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error syncing Drive resource {GoogleId}", claim.FolderId);
            diff = BuildDiff(claim, [], ex.Message);
        }

        if (action == SyncAction.Execute && claim.Resources.Count > 0)
        {
            var ids = claim.Resources.Select(r => r.Id).ToList();
            if (diff.ErrorMessage is not null)
                await resourceRepository.SetErrorMessageManyAsync(ids, diff.ErrorMessage, ct);
            else if (await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct) != SyncMode.None)
                await resourceRepository.MarkSyncedManyAsync(ids, clock.GetCurrentInstant(), ct);
        }
        return diff;
    }

    private async Task DeactivateRetiredTeamResourcesAsync(
        IReadOnlyList<GoogleResource> resources, IReadOnlyList<ResourceSyncDiff> diffs, CancellationToken ct)
    {
        var teamsById = await teamService.GetTeamsAsync(ct);
        var diffsById = diffs.Where(d => d.GoogleId is not null)
            .ToDictionary(d => d.GoogleId!, StringComparer.Ordinal);
        foreach (var group in resources.Where(r => teamsById.TryGetValue(r.TeamId, out var team) && !team.IsActive)
            .GroupBy(r => (r.TeamId, r.ResourceType)))
        {
            if (group.All(r => diffsById.TryGetValue(r.GoogleId, out var diff) && diff.ErrorMessage is null))
                await teamResourceService.DeactivateResourcesForTeamAsync(group.Key.TeamId, group.Key.ResourceType, ct);
        }
    }

    private async Task HydrateExtraMembersAsync(List<MemberSyncStatus> members, CancellationToken ct)
    {
        var extraEmails = members.Where(m => m.State == MemberSyncState.Extra)
            .Select(m => m.Email).Distinct(GmailAliasEmailComparer.Instance).ToList();
        if (extraEmails.Count == 0) return;
        var owners = UserEmailMatchOwner.ByEmail(await userEmailService.MatchByEmailsAsync(extraEmails, ct));
        var users = await userService.GetUserInfosAsync(owners.Values.Select(m => m.UserId).Distinct().ToList(), ct);
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member.State == MemberSyncState.Extra && owners.TryGetValue(member.Email, out var owner)
                && users.TryGetValue(owner.UserId, out var user))
                members[i] = member with { UserId = user.Id, DisplayName = user.BurnerName, ProfilePictureUrl = user.ProfilePictureUrl };
        }
    }

    private static ResourceSyncDiff BuildDiff(FolderClaim claim, List<MemberSyncStatus> members, string? error = null)
    {
        var primary = claim.Resources.FirstOrDefault();
        return new ResourceSyncDiff
        {
            ResourceId = primary?.Id ?? Guid.Empty,
            ResourceName = primary?.Name ?? claim.FolderId,
            ResourceType = (primary?.ResourceType ?? GoogleResourceType.DriveFolder).ToString(),
            GoogleId = claim.FolderId, Url = primary?.Url, ErrorMessage = error,
            PermissionLevel = primary is { DrivePermissionLevel: not DrivePermissionLevel.None }
                ? primary.DrivePermissionLevel.ToString() : null,
            LinkedTeams = claim.Claim.LinkedTeams?.ToList() ?? [], Members = members
        };
    }

    private async Task<ResourceSyncDiff> ReconcileClaimAsync(
        FolderClaim claim,
        SyncAction action,
        GoogleSyncSource syncSource,
        CancellationToken ct)
    {
        var expectedMembers = await HydrateExpectedMembersAsync(claim.Access, ct);

        var permsResult = await drivePermissions.ListPermissionsAsync(claim.FolderId, ct);
        if (permsResult.Permissions is null)
        {
            var error = $"Google Drive permission list failed (HTTP {permsResult.Error?.StatusCode}): {permsResult.Error?.RawMessage}";
            logger.LogWarning("Google Drive access sync failed for {FolderId}: {Error}", claim.FolderId, error);
            return BuildDiff(claim, [], error);
        }

        var plan = BuildPlan(expectedMembers, permsResult.Permissions);
        for (var i = 0; i < plan.Members.Count; i++)
        {
            var member = plan.Members[i];
            if (member.UserId is { } userId && claim.Claim.MemberTeamLinks?.TryGetValue(userId, out var links) == true)
                plan.Members[i] = member with { TeamLinks = links.ToList() };
        }
        await HydrateExtraMembersAsync(plan.Members, ct);
        var errors = new List<string>();

        if (action == SyncAction.Execute)
        {
            var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct);
            if (mode != SyncMode.None)
            {
                errors.AddRange(await ApplyMissingAndChangedAsync(claim, plan, mode, syncSource, ct));

                if (mode == SyncMode.AddAndRemove)
                    errors.AddRange(await ApplyExtraAsync(claim, plan, syncSource, ct));
            }
        }

        return BuildDiff(claim, plan.Members, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private async Task<Dictionary<Guid, DriveExpectedMember>> HydrateExpectedMembersAsync(
        IReadOnlyDictionary<Guid, DrivePermissionLevel> access,
        CancellationToken ct)
    {
        var userIds = access.Keys.ToList();
        if (userIds.Count == 0)
            return [];

        var users = await userService.GetUserInfosAsync(userIds, ct);
        var emailsByUserId = await userEmailService.GetEntitiesByUserIdsAsync(userIds, ct);

        var result = new Dictionary<Guid, DriveExpectedMember>();
        foreach (var (userId, level) in access)
        {
            if (level == DrivePermissionLevel.None)
                continue;
            if (!users.TryGetValue(userId, out var user))
                continue;
            // #1704: the read resolves merges forward, so a tombstone id answers with the
            // survivor and the MergedToUserId test can no longer see one. Comparing the row
            // back against the requested id keeps this the skip it has always been: an ACL
            // entry keyed to an archived id grants nobody, least of all silently granting the
            // human who absorbed it.
            if (user.Id != userId || user.GoogleEmailStatus == GoogleEmailStatus.Rejected || user.IsDeletionPending)
                continue;
            if (user.IsSuspended)
                continue;

            var emails = emailsByUserId.TryGetValue(userId, out var list) ? list : [];
            var email = emails
                .Where(e => e.IsVerified && e.IsGoogle)
                .Select(e => e.Email)
                .FirstOrDefault()
                ?? emails
                    .Where(e => e.IsVerified && e.Provider != null)
                    .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Email)
                    .FirstOrDefault();
            if (email is null)
                continue;

            // Drive rejects a Gmail "+tag" address and reports/grants the
            // canonical form (#945), matching the immediate join gateway.
            var canonicalEmail = EmailNormalization.CanonicalizeGmail(email);
            result[userId] = new DriveExpectedMember(userId, canonicalEmail, user.BurnerName, user.ProfilePictureUrl, level);
        }

        return result;
    }

    private static DrivePlan BuildPlan(
        IReadOnlyDictionary<Guid, DriveExpectedMember> expectedMembers,
        IReadOnlyList<DrivePermission> permissions)
    {
        // Drive canonicalizes Gmail aliases; union users resolving to the same address
        // before planning so the highest claim wins rather than producing duplicate grants.
        var byEmail = expectedMembers.Values.GroupBy(m => m.Email, NormalizingEmailComparer.Instance)
            .ToDictionary(g => g.Key, g => g.MaxBy(m => m.Level)!, NormalizingEmailComparer.Instance);

        var allEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
        var directEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
        var roleByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
        var idByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
        var inheritedLevelByEmail = new Dictionary<string, DrivePermissionLevel>(NormalizingEmailComparer.Instance);

        foreach (var perm in permissions)
        {
            if (!DrivePermissionRoleMapper.IsAnyUserPermission(perm))
                continue;

            var email = EmailNormalization.CanonicalizeGmail(perm.EmailAddress!);
            allEmails.Add(email);
            if (!string.IsNullOrEmpty(perm.Role))
                roleByEmail[email] = perm.Role;
            // Keep the inherited floor separate from the effective role: the latter
            // can include a direct elevation that this folder must later revoke.
            var inheritedLevels = perm.InheritedRoles.Select(DrivePermissionRoleMapper.Parse).ToArray();
            if (inheritedLevels.Length > 0 && inheritedLevels.All(level => level.HasValue))
                inheritedLevelByEmail[email] = inheritedLevels.Max()!.Value;
            if (IsDirectManagedPermission(perm) && perm.Id is not null &&
                (!perm.HasInheritedComponent || inheritedLevelByEmail.ContainsKey(email)))
            {
                directEmails.Add(email);
                idByEmail[email] = perm.Id;
            }
        }

        var members = new List<MemberSyncStatus>();
        foreach (var member in byEmail.Values)
        {
            roleByEmail.TryGetValue(member.Email, out var currentRole);
            inheritedLevelByEmail.TryGetValue(member.Email, out var inheritedLevel);
            var expectedRole = (inheritedLevel > member.Level ? inheritedLevel : member.Level).ToApiRole();

            MemberSyncState state;
            if (!allEmails.Contains(member.Email))
                state = MemberSyncState.Missing;
            else if (!directEmails.Contains(member.Email))
                // An inherited permission cannot be edited or deleted at this level (#945), but
                // it can be *out-ranked* by a direct grant. Workgroup folders live under a root
                // that hands every Colaborador/Asociado an inherited Viewer, so a member who is
                // one would otherwise never be raised to Contributor. Below the expected level
                // the folder still needs a direct permission created, so classify it Missing;
                // at or above it, inheritance already satisfies the claim.
                state = DrivePermissionRoleMapper.Parse(currentRole) is { } inherited && inherited < member.Level
                    ? MemberSyncState.Missing
                    : MemberSyncState.Inherited;
            else
                state = inheritedLevel >= member.Level && DrivePermissionRoleMapper.Parse(currentRole) == inheritedLevel
                    ? MemberSyncState.Inherited
                    : string.Equals(currentRole, expectedRole, StringComparison.Ordinal)
                        ? MemberSyncState.Correct
                        : MemberSyncState.WrongRole;

            members.Add(new MemberSyncStatus(
                member.Email, member.DisplayName, state, [],
                currentRole, expectedRole, member.UserId, member.ProfilePictureUrl));
        }

        var extraEmails = directEmails.Where(email => !byEmail.ContainsKey(email)).ToList();
        foreach (var email in extraEmails)
        {
            roleByEmail.TryGetValue(email, out var extraRole);
            var inheritedRole = inheritedLevelByEmail.TryGetValue(email, out var inheritedLevel)
                ? inheritedLevel.ToApiRole()
                : null;
            // A mixed permission already reduced to its inherited floor grants
            // nothing beyond the parent. It cannot be deleted here (#945).
            if (inheritedRole is not null && DrivePermissionRoleMapper.Parse(extraRole) <= inheritedLevel)
                continue;
            members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, [], extraRole, inheritedRole));
        }

        return new DrivePlan(members, idByEmail);
    }

    private async Task<List<string>> ApplyMissingAndChangedAsync(FolderClaim claim, DrivePlan plan, SyncMode mode, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var member in plan.Members.Where(m => m.State is MemberSyncState.Missing or MemberSyncState.WrongRole))
        {
            if (member.State == MemberSyncState.WrongRole && plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
            {
                // AddOnly can elevate access but cannot reduce it. Unknown roles also
                // wait for AddAndRemove rather than assuming a change is an elevation.
                if (mode == SyncMode.AddOnly &&
                    !(DrivePermissionRoleMapper.Parse(member.CurrentRole) < DrivePermissionRoleMapper.Parse(member.ExpectedRole)))
                    continue;
                if (await UpdateAndLogAsync(claim, member, permissionId, syncSource, ct) is { } updateError) errors.Add(updateError);
                continue;
            }

            var role = member.ExpectedRole!;
            var result = await drivePermissions.CreatePermissionAsync(claim.FolderId, member.Email, role, ct);
            switch (result.Outcome)
            {
                case DrivePermissionCreateOutcome.Created:
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.ResourceId,
                        $"Granted Drive access ({role}) to {member.Email} ({claim.FolderId})",
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, syncSource, success: true,
                        userId: member.UserId, ct: ct);
                    break;
                case DrivePermissionCreateOutcome.AlreadyExists:
                    logger.LogDebug("Permission already exists for {Email} on {FolderId}", member.Email, claim.FolderId);
                    break;
                case DrivePermissionCreateOutcome.Failed:
                    var error = $"Google Drive add failed for {member.Email} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                    logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, member.Email, error);
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.ResourceId, error,
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, syncSource, success: false,
                        errorMessage: error, userId: member.UserId, ct: ct);
                    errors.Add(error);
                    if (result.Error is { IsDriveTargetRejection: true } && member.UserId is { } rejectedUserId)
                        await userService.TrySetGoogleEmailStatusFromSyncAsync(rejectedUserId, GoogleEmailStatus.Rejected, ct);
                    break;
            }
        }
        return errors;
    }

    private async Task<List<string>> ApplyExtraAsync(FolderClaim claim, DrivePlan plan, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var member in plan.Members.Where(m => m.State == MemberSyncState.Extra))
        {
            if (!plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                continue;

            if (member.ExpectedRole is not null)
            {
                // This is a mixed permission: only the direct elevation is extra.
                // Preserve the inherited floor and do not claim all access was removed.
                if (await UpdateAndLogAsync(claim, member, permissionId, syncSource, ct) is { } error) errors.Add(error);
                continue;
            }

            // Telling someone their access was removed when the delete failed (or the
            // permission turned out to be inherited and untouchable) is a false notice.
            var deletion = await DeleteAndLogAsync(claim, member.Email, member.UserId, permissionId, syncSource, ct);
            if (deletion.Error is not null) errors.Add(deletion.Error);
            if (deletion.Deleted) await NotifyRemovalAsync(member.Email, claim, ct);
        }
        return errors;
    }

    private async Task<string?> UpdateAndLogAsync(FolderClaim claim, MemberSyncStatus member, string permissionId, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var role = member.ExpectedRole!;
        var error = await drivePermissions.UpdatePermissionAsync(claim.FolderId, permissionId, role, ct);
        var action = DrivePermissionRoleMapper.Parse(member.CurrentRole) > DrivePermissionRoleMapper.Parse(role)
            ? GoogleSyncLogAction.AccessRevoked
            : GoogleSyncLogAction.AccessGranted;
        var description = error is null
            ? $"Changed Drive access for {member.Email} from {member.CurrentRole} to {role} ({claim.FolderId})"
            : $"Google Drive role change failed for {member.Email} (HTTP {error.StatusCode}): {error.RawMessage}";
        if (error is not null)
            logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}",
                claim.FolderId, member.Email, description);

        await googleSyncLog.LogAsync(
            action, claim.ResourceId, description, nameof(GoogleDriveAccessSyncService),
            member.Email, role, syncSource, success: error is null,
            errorMessage: error is null ? null : description, userId: member.UserId, ct: ct);
        return error is null ? null : description;
    }

    /// <summary>Deletes one permission and logs the outcome. True only when Drive actually removed it.</summary>
    private async Task<(bool Deleted, string? Error)> DeleteAndLogAsync(FolderClaim claim, string email, Guid? userId, string permissionId, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var result = await drivePermissions.DeletePermissionAsync(claim.FolderId, permissionId, ct);
        switch (result.Outcome)
        {
            case DrivePermissionDeleteOutcome.Deleted:
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, claim.ResourceId,
                    $"Removed {email} from Drive folder {claim.FolderId}",
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", syncSource, success: true,
                    userId: userId, ct: ct);
                return (true, null);
            case DrivePermissionDeleteOutcome.InheritedPermission:
                logger.LogWarning(
                    "Skipping removal of {Email} from {FolderId} — permission is inherited, not direct",
                    email, claim.FolderId);
                break;
            case DrivePermissionDeleteOutcome.Failed:
                var error = $"Google Drive remove failed for {email} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, email, error);
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, claim.ResourceId, error,
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", syncSource, success: false,
                    errorMessage: error, userId: userId, ct: ct);
                return (false, error);
        }

        return (false, null);
    }

    private async Task NotifyRemovalAsync(string email, FolderClaim claim, CancellationToken ct)
    {
        try
        {
            await removalNotifications.NotifyRemovalAsync(
                email, claim.Resources.FirstOrDefault()?.ResourceType ?? GoogleResourceType.DriveFolder,
                claim.Resources.FirstOrDefault()?.Name, claim.Resources.FirstOrDefault()?.Url ?? claim.FolderId,
                SyncRemovalReason.Reconciliation, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue Drive removal notification for {Email} from {FolderId}", email, claim.FolderId);
        }
    }

    private async Task<ResourceSyncDiff> BuildCollisionDiffAsync(FolderClaim claim)
    {
        var error = $"Google Drive access source collision for {claim.FolderId}: {string.Join(", ", claim.SourceNames)}";
        logger.LogError("{Error}", error);

        await auditLogService.LogAsync(
            AuditAction.AnomalousPermissionDetected,
            (claim.Resources.FirstOrDefault()?.ResourceType ?? GoogleResourceType.DriveFolder).ToString(),
            claim.ResourceId,
            error,
            nameof(GoogleDriveAccessSyncService));

        return BuildDiff(claim, [], error);
    }

    private static bool IsDirectManagedPermission(DrivePermission perm)
    {
        if (!DrivePermissionRoleMapper.IsAnyUserPermission(perm))
            return false;
        if (string.Equals(perm.Role, "owner", StringComparison.OrdinalIgnoreCase))
            return false;

        // Mixed permissions cannot be deleted (#945), but their direct elevation
        // can be updated down to the inherited floor.
        return perm.HasDirectComponent;
    }

    internal sealed record FolderClaim(string FolderId, int ClaimCount, string[] SourceNames,
        GoogleDriveAccessClaim Claim, IReadOnlyList<GoogleResource> Resources)
    {
        public bool IsCollision => ClaimCount > 1;
        public Dictionary<Guid, DrivePermissionLevel> Access => Claim.Access;
        public Guid ResourceId => Resources.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    private sealed record DriveExpectedMember(Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, DrivePermissionLevel Level);

    private sealed record DrivePlan(List<MemberSyncStatus> Members, Dictionary<string, string> PermissionIdByEmail);
}
