using Humans.Base.Attributes;
using Humans.GoogleIntegration.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.GoogleIntegration.Services.Workspace;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// <see cref="IGoogleDriveSync"/> impl: reconciles Drive folders claimed
/// through <see cref="IGoogleDriveAccessSource"/> — the source fan-out.
/// Sections supply membership, permission policy and display facts; this service
/// owns the shared permission planner, mutations and reconciliation history.
/// </summary>
[CrossSectionWrite("Drive sync writes Google email status back to the user.")]
internal sealed class GoogleDriveAccessSyncService(
    IEnumerable<IGoogleDriveAccessSource> sources,
    IGoogleDrivePermissionsClient drivePermissions,
    IUserService userService,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleRemovalNotificationService removalNotifications,
    ITeamResourceService resourceService,
    IClock clock,
    ILogger<GoogleDriveAccessSyncService> logger) : IGoogleDriveSync
{
    public async Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var claims = await LoadClaimsAsync(folderId: null, ct);
        var diffs = new List<ResourceSyncDiff>();

        foreach (var claim in claims)
        {
            diffs.Add(claim.IsCollision
                ? await BuildCollisionDiffAsync(claim)
                : await ReconcileClaimAsync(claim with { SyncSource = syncSource }, action, ct));
        }

        if (action == SyncAction.Execute &&
            await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct) == SyncMode.AddAndRemove)
        {
            var failed = diffs.Where(d => d.ErrorMessage is not null).Select(d => d.GoogleId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var group in claims.SelectMany(c => c.Resources.Where(r => c.RetiredResourceIds.Contains(r.Id)))
                .GroupBy(r => (r.TeamId, r.ResourceType)))
            {
                if (group.All(r => !failed.Contains(r.GoogleId)))
                    await resourceService.DeactivateResourcesForTeamAsync(group.Key.TeamId, group.Key.ResourceType, ct);
            }
        }
        return new SyncPreviewResult { Diffs = diffs };
    }

    public async Task<ResourceSyncDiff> ReconcileOneAsync(
        string folderId,
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var claims = await LoadClaimsAsync(folderId, ct);
        var claim = claims.SingleOrDefault(c =>
            string.Equals(c.FolderId, folderId, StringComparison.Ordinal));

        if (claim is null)
        {
            const string error = "No Google Drive access source claims this folder";
            logger.LogWarning("Google Drive access sync: no source claims folder id {FolderId}", folderId);
            return BuildErrorDiff(folderId, error);
        }

        return claim.IsCollision
            ? await BuildCollisionDiffAsync(claim)
            : await ReconcileClaimAsync(claim with { SyncSource = syncSource }, action, ct);
    }

    /// <summary>
    /// Loads every source before planning changes. A failed source leaves ownership
    /// unknown, so no partial union may be used to remove another source's access.
    /// </summary>
    private async Task<IReadOnlyList<FolderClaim>> LoadClaimsAsync(string? folderId, CancellationToken ct)
    {
        var claims = new List<(string SourceName, string FolderId, GoogleDriveAccessClaim Access)>();

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
                    "Google Drive access source {SourceType} threw while computing expected access; aborting reconciliation",
                    source.GetType().Name);
                throw;
            }

            foreach (var (key, access) in expected)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                claims.Add((source.GetType().Name, key.Trim(), access));
            }
        }

        return claims
            .GroupBy(c => c.FolderId, StringComparer.Ordinal)
            .Select(g =>
            {
                var access = new Dictionary<Guid, DrivePermissionLevel>();
                var preserveHigher = new HashSet<Guid>();
                var memberTeams = new Dictionary<Guid, List<TeamLink>>();
                foreach (var source in g)
                {
                    foreach (var (userId, level) in source.Access.Access)
                    {
                        if (!access.TryGetValue(userId, out var current) || level > current)
                            access[userId] = level;
                        if (source.Access.PreserveHigherPermissions)
                            preserveHigher.Add(userId);
                    }
                    foreach (var (userId, links) in source.Access.MemberTeams)
                    {
                        if (!memberTeams.TryGetValue(userId, out var merged))
                            memberTeams[userId] = merged = [];
                        merged.AddRange(links);
                    }
                }
                return new FolderClaim(g.Key,
                    g.Count() > 1 && !g.Any(c => c.Access.AllowSharedAccess),
                    g.Select(c => c.SourceName).Distinct(StringComparer.Ordinal).ToArray(),
                    access, preserveHigher,
                    access.Keys.Where(userId => g.Where(c => c.Access.Access.ContainsKey(userId))
                        .All(c => c.Access.PreserveInheritedPermissions)).ToHashSet(),
                    g.All(c => c.Access.PreserveInheritedPermissions), memberTeams,
                    g.SelectMany(c => c.Access.Resources).DistinctBy(r => r.Id).ToList(),
                    g.SelectMany(c => c.Access.RetiredResourceIds).ToHashSet(),
                    g.SelectMany(c => c.Access.LinkedTeams).Distinct().ToList());
            })
            .ToList();
    }

    private async Task<ResourceSyncDiff> ReconcileClaimAsync(
        FolderClaim claim,
        SyncAction action,
        CancellationToken ct)
    {
        try
        {
            var expectedMembers = await HydrateExpectedMembersAsync(claim, ct);

            var permsResult = await drivePermissions.ListPermissionsAsync(claim.FolderId, ct);
            if (permsResult.Permissions is null)
            {
                var error = $"Google Drive permission list failed (HTTP {permsResult.Error?.StatusCode}): {permsResult.Error?.RawMessage}";
                logger.LogWarning("Google Drive access sync failed for {FolderId}: {Error}", claim.FolderId, error);
                if (action == SyncAction.Execute)
                    foreach (var resource in claim.Resources)
                        await resourceService.RecordResourceErrorAsync(resource.Id, error, ct);
                return BuildClaimDiff(claim, [], error);
            }

            var plan = await BuildPlanAsync(claim, expectedMembers, permsResult.Permissions, ct);

            var errors = new List<string>();
            if (action == SyncAction.Execute)
            {
                var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct);
                if (mode != SyncMode.None)
                {
                    await ApplyMissingAndChangedAsync(claim, plan, mode, errors, ct);

                    if (mode == SyncMode.AddAndRemove)
                        await ApplyExtraAsync(claim, plan, errors, ct);
                }
            }

            var errorMessage = errors.Count == 0 ? null : string.Join("; ", errors);
            if (action == SyncAction.Execute)
            {
                foreach (var resource in claim.Resources)
                {
                    if (errorMessage is null)
                        await resourceService.MarkResourceSyncedAsync(resource.Id, clock.GetCurrentInstant(), ct);
                    else
                        await resourceService.RecordResourceErrorAsync(resource.Id, errorMessage, ct);
                }
            }
            return BuildClaimDiff(claim, plan.Members, errorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error reconciling Drive item {FolderId}", claim.FolderId);
            if (action == SyncAction.Execute)
                foreach (var resource in claim.Resources)
                    await resourceService.RecordResourceErrorAsync(resource.Id, ex.Message, ct);
            return BuildClaimDiff(claim, [], ex.Message);
        }
    }

    private static ResourceSyncDiff BuildClaimDiff(FolderClaim claim, List<MemberSyncStatus> members, string? error = null) => new()
    {
        ResourceId = claim.Resources.FirstOrDefault()?.Id ?? Guid.Empty,
        ResourceName = claim.Resources.FirstOrDefault()?.Name ?? claim.FolderId,
        ResourceType = (claim.Resources.FirstOrDefault()?.ResourceType ?? GoogleResourceType.DriveFolder).ToString(),
        Url = claim.Resources.FirstOrDefault()?.Url,
        PermissionLevel = claim.Resources.FirstOrDefault()?.DrivePermissionLevel.ToString(),
        LinkedTeams = claim.LinkedTeams,
        GoogleId = claim.FolderId,
        Members = members,
        ErrorMessage = error
    };

    private async Task<Dictionary<Guid, DriveExpectedMember>> HydrateExpectedMembersAsync(
        FolderClaim claim,
        CancellationToken ct)
    {
        var access = claim.Access;
        var eligibleMembers = await GoogleSyncMemberHydration.LoadAsync(
            access.Keys.ToList(), userService, ct);
        var result = new Dictionary<Guid, DriveExpectedMember>();
        foreach (var (userId, level) in access)
        {
            if (level != DrivePermissionLevel.None && eligibleMembers.TryGetValue(userId, out var member))
                result[userId] = new DriveExpectedMember(
                    member.UserId, member.Email, member.DisplayName, member.ProfilePictureUrl, level, member.GoogleEmailId,
                    claim.PreserveHigherPermissions.Contains(userId),
                    claim.InheritedProtectedUsers.Contains(userId),
                    claim.MemberTeams.GetValueOrDefault(userId, []).Distinct().ToList());
        }

        return result;
    }

    private async Task<DrivePlan> BuildPlanAsync(
        FolderClaim claim,
        IReadOnlyDictionary<Guid, DriveExpectedMember> expectedMembers,
        IReadOnlyList<DrivePermission> permissions,
        CancellationToken ct)
    {
        // Distinct member IDs may carry aliases that Google resolves to one address.
        // Reconcile that address once at the highest level already claimed by a source.
        var byEmail = expectedMembers.Values
            .GroupBy(member => member.Email, GmailAliasEmailComparer.Instance)
            .ToDictionary(group => group.Key,
                group =>
                {
                    var strongest = group.OrderByDescending(member => member.Level)
                        .ThenBy(member => member.UserId).First();
                    return strongest with
                    {
                        PreserveHigherPermissions = group.Any(member => member.PreserveHigherPermissions),
                        PreserveInheritedPermissions = group.All(member => member.PreserveInheritedPermissions),
                        TeamLinks = group.SelectMany(member => member.TeamLinks).Distinct().ToList()
                    };
                },
                GmailAliasEmailComparer.Instance);

        var inheritedEmails = new HashSet<string>(GmailAliasEmailComparer.Instance);
        var allEmails = new HashSet<string>(GmailAliasEmailComparer.Instance);
        var directEmails = new HashSet<string>(GmailAliasEmailComparer.Instance);
        var roleByEmail = new Dictionary<string, string>(GmailAliasEmailComparer.Instance);
        var idByEmail = new Dictionary<string, string>(GmailAliasEmailComparer.Instance);
        var inheritedLevelByEmail = new Dictionary<string, DrivePermissionLevel>(GmailAliasEmailComparer.Instance);

        foreach (var perm in permissions)
        {
            if (!DrivePermissionRoleMapper.IsAnyUserPermission(perm))
                continue;

            var email = EmailNormalization.CanonicalizeGmail(perm.EmailAddress!);
            allEmails.Add(email);
            if (perm.HasInheritedComponent)
                inheritedEmails.Add(email);
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
            var currentLevel = DrivePermissionRoleMapper.Parse(currentRole);
            inheritedLevelByEmail.TryGetValue(member.Email, out var inheritedLevel);
            var expectedRole = (inheritedLevel > member.Level ? inheritedLevel : member.Level).ToApiRole();

            MemberSyncState state;
            if (!allEmails.Contains(member.Email))
                state = MemberSyncState.Missing;
            else if (member.PreserveInheritedPermissions && inheritedEmails.Contains(member.Email))
                state = MemberSyncState.Inherited;
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
                    : (string.Equals(currentRole, expectedRole, StringComparison.Ordinal) ||
                        member.PreserveHigherPermissions && (!currentLevel.HasValue || currentLevel >= member.Level))
                        ? MemberSyncState.Correct
                        : MemberSyncState.WrongRole;

            members.Add(new MemberSyncStatus(
                member.Email, member.DisplayName, state, member.TeamLinks,
                currentRole, expectedRole, member.UserId, member.ProfilePictureUrl));
        }

        var serviceAccountEmail = await resourceService.GetServiceAccountEmailAsync(ct);
        var extraEmails = (claim.Resources.Count > 0 ? allEmails : directEmails)
            .Where(email => !byEmail.ContainsKey(email) &&
            !GmailAliasEmailComparer.Instance.Equals(email, serviceAccountEmail)).ToList();
        var extraOwners = UserEmailMatchOwner.ByEmail(extraEmails.Count == 0
            ? []
            : await userEmailService.MatchByEmailsAsync(extraEmails, ct));
        var extraUserIds = extraOwners.Values.Select(owner => owner.UserId).Distinct().ToList();
        var extraUsers = extraUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(extraUserIds, ct);
        foreach (var email in extraEmails)
        {
            roleByEmail.TryGetValue(email, out var extraRole);
            var inheritedRole = inheritedLevelByEmail.TryGetValue(email, out var inheritedLevel)
                ? inheritedLevel.ToApiRole()
                : null;
            var preserveInherited = claim.PreserveInheritedPermissions && inheritedEmails.Contains(email);
            if (!directEmails.Contains(email) || preserveInherited)
            {
                if (claim.Resources.Count > 0)
                {
                    var inheritedOwner = extraOwners.TryGetValue(email, out var found) ? found.UserId : (Guid?)null;
                    var inheritedUser = inheritedOwner.HasValue ? extraUsers.GetValueOrDefault(inheritedOwner.Value) : null;
                    members.Add(new MemberSyncStatus(email, inheritedUser?.BurnerName ?? email, MemberSyncState.Inherited,
                        [], extraRole, UserId: inheritedOwner, ProfilePictureUrl: inheritedUser?.ProfilePictureUrl));
                }
                continue;
            }
            // A mixed permission already reduced to its inherited floor grants
            // nothing beyond the parent. It cannot be deleted here (#945).
            if (inheritedRole is not null && DrivePermissionRoleMapper.Parse(extraRole) <= inheritedLevel)
                continue;
            var ownerId = extraOwners.TryGetValue(email, out var owner) ? owner.UserId : (Guid?)null;
            var user = ownerId.HasValue ? extraUsers.GetValueOrDefault(ownerId.Value) : null;
            members.Add(new MemberSyncStatus(email, user?.BurnerName ?? email, MemberSyncState.Extra, [], extraRole, inheritedRole,
                UserId: ownerId, ProfilePictureUrl: user?.ProfilePictureUrl));
        }

        return new DrivePlan(members, idByEmail,
            byEmail.ToDictionary(kv => kv.Key, kv => kv.Value.GoogleEmailId, GmailAliasEmailComparer.Instance));
    }

    private async Task ApplyMissingAndChangedAsync(FolderClaim claim, DrivePlan plan, SyncMode mode, List<string> errors, CancellationToken ct)
    {
        foreach (var member in plan.Members.Where(m => m.State is MemberSyncState.Missing or MemberSyncState.WrongRole))
        {
            if (member.State == MemberSyncState.WrongRole && plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
            {
                // AddOnly can elevate access but cannot reduce it. Unknown roles also
                // wait for AddAndRemove rather than assuming a change is an elevation.
                if (mode == SyncMode.AddOnly &&
                    !(DrivePermissionRoleMapper.Parse(member.CurrentRole) < DrivePermissionRoleMapper.Parse(member.ExpectedRole)))
                    continue;
                await UpdateAndLogAsync(claim, member, permissionId, errors, ct);
                continue;
            }

            var role = member.ExpectedRole!;
            var result = await drivePermissions.CreatePermissionAsync(claim.FolderId, member.Email, role, ct);
            switch (result.Outcome)
            {
                case DrivePermissionCreateOutcome.Created:
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.Resources.FirstOrDefault()?.Id ?? Guid.Empty,
                        $"Granted Drive access ({role}) to {member.Email} ({claim.FolderId})",
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, claim.SyncSource, success: true,
                        userId: member.UserId, ct: ct);
                    break;
                case DrivePermissionCreateOutcome.AlreadyExists:
                    logger.LogDebug("Permission already exists for {Email} on {FolderId}", member.Email, claim.FolderId);
                    break;
                case DrivePermissionCreateOutcome.Failed:
                    var error = $"Google Drive add failed for {MaskEmail(member.Email)} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                    errors.Add(error);
                    logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, member.Email, error);
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.Resources.FirstOrDefault()?.Id ?? Guid.Empty, error,
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, claim.SyncSource, success: false,
                        errorMessage: error, userId: member.UserId, ct: ct);
                    if (member.UserId is { } userId && DrivePermissionErrorPolicy.IsTargetRejection(result.Error))
                        await userService.TrySetGoogleEmailStatusFromSyncAsync(userId, GoogleEmailStatus.Rejected, ct, plan.GoogleEmailIdsByEmail[member.Email]);
                    break;
            }
        }
    }

    /// <summary>Keeps two characters of the local part, like the log pipeline's email redaction.</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[..Math.Min(2, at)]}***{email[at..]}";
    }

    private async Task ApplyExtraAsync(FolderClaim claim, DrivePlan plan, List<string> errors, CancellationToken ct)
    {
        foreach (var member in plan.Members.Where(m => m.State == MemberSyncState.Extra))
        {
            if (!plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                continue;

            if (member.ExpectedRole is not null)
            {
                // This is a mixed permission: only the direct elevation is extra.
                // Preserve the inherited floor and do not claim all access was removed.
                await UpdateAndLogAsync(claim, member, permissionId, errors, ct);
                continue;
            }

            // Telling someone their access was removed when the delete failed (or the
            // permission turned out to be inherited and untouchable) is a false notice.
            if (await DeleteAndLogAsync(claim, member.Email, member.UserId, permissionId, errors, ct))
                await NotifyRemovalAsync(member.Email, claim, ct);
        }
    }

    private async Task UpdateAndLogAsync(FolderClaim claim, MemberSyncStatus member, string permissionId, List<string> errors, CancellationToken ct)
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
            errors.Add(description);
        if (error is not null)
            logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}",
                claim.FolderId, member.Email, description);

        await googleSyncLog.LogAsync(
            action, claim.Resources.FirstOrDefault()?.Id ?? Guid.Empty, description, nameof(GoogleDriveAccessSyncService),
            member.Email, role, claim.SyncSource, success: error is null,
            errorMessage: error is null ? null : description, userId: member.UserId, ct: ct);
    }

    /// <summary>Deletes one permission and logs the outcome. True only when Drive actually removed it.</summary>
    private async Task<bool> DeleteAndLogAsync(FolderClaim claim, string email, Guid? userId, string permissionId, List<string> errors, CancellationToken ct)
    {
        var result = await drivePermissions.DeletePermissionAsync(claim.FolderId, permissionId, ct);
        switch (result.Outcome)
        {
            case DrivePermissionDeleteOutcome.Deleted:
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, claim.Resources.FirstOrDefault()?.Id ?? Guid.Empty,
                    $"Removed {email} from Drive folder {claim.FolderId}",
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", claim.SyncSource, success: true,
                    userId: userId, ct: ct);
                return true;
            case DrivePermissionDeleteOutcome.InheritedPermission:
                logger.LogWarning(
                    "Skipping removal of {Email} from {FolderId} — permission is inherited, not direct",
                    email, claim.FolderId);
                break;
            case DrivePermissionDeleteOutcome.Failed:
                var error = $"Google Drive remove failed for {email} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                errors.Add(error);
                logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, email, error);
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, claim.Resources.FirstOrDefault()?.Id ?? Guid.Empty, error,
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", claim.SyncSource, success: false,
                    errorMessage: error, userId: userId, ct: ct);
                break;
        }

        return false;
    }

    private async Task NotifyRemovalAsync(string email, FolderClaim claim, CancellationToken ct)
    {
        try
        {
            await removalNotifications.NotifyRemovalAsync(
                email, claim.Resources.FirstOrDefault()?.ResourceType ?? GoogleResourceType.DriveFolder,
                claim.Resources.FirstOrDefault()?.Name, claim.FolderId,
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
            GoogleResourceType.DriveFolder.ToString(),
            Guid.Empty,
            error,
            nameof(GoogleDriveAccessSyncService));

        return BuildClaimDiff(claim, [], error);
    }

    private static ResourceSyncDiff BuildErrorDiff(string folderId, string error) => new()
    {
        ResourceId = Guid.Empty,
        ResourceName = folderId,
        ResourceType = GoogleResourceType.DriveFolder.ToString(),
        GoogleId = folderId,
        ErrorMessage = error
    };

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

    private sealed record FolderClaim(
        string FolderId, bool IsCollision, string[] SourceNames,
        Dictionary<Guid, DrivePermissionLevel> Access, HashSet<Guid> PreserveHigherPermissions,
        HashSet<Guid> InheritedProtectedUsers, bool PreserveInheritedPermissions,
        Dictionary<Guid, List<TeamLink>> MemberTeams,
        List<GoogleResourceSnapshot> Resources, HashSet<Guid> RetiredResourceIds, List<TeamLink> LinkedTeams)
    {
        public GoogleSyncSource SyncSource { get; init; } = GoogleSyncSource.ScheduledSync;
    }

    private sealed record DriveExpectedMember(
        Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl,
        DrivePermissionLevel Level, Guid GoogleEmailId, bool PreserveHigherPermissions, bool PreserveInheritedPermissions, List<TeamLink> TeamLinks);

    private sealed record DrivePlan(
        List<MemberSyncStatus> Members, Dictionary<string, string> PermissionIdByEmail,
        Dictionary<string, Guid> GoogleEmailIdsByEmail);
}
