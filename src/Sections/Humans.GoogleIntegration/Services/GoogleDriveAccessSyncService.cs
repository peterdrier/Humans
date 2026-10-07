using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;
using Humans.Base.Attributes;
using Humans.GoogleIntegration.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// <see cref="IGoogleDriveSync"/> impl: reconciles Drive folders claimed
/// through <see cref="IGoogleDriveAccessSource"/> — the source fan-out.
/// Sources own membership rules; this service owns identity hydration, permission
/// diffing/mutation, resource metadata and audit logs for folders and files.
/// </summary>
[CrossSectionWrite("Drive sync records Google target-email rejection in Users.")]
internal sealed class GoogleDriveAccessSyncService(
    IEnumerable<IGoogleDriveAccessSource> sources,
    IGoogleDrivePermissionsClient drivePermissions,
    IUserService userService,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleRemovalNotificationService removalNotifications,
    ILogger<GoogleDriveAccessSyncService> logger,
    IGoogleResourceRepository resourceRepository,
    ITeamServiceRead teams,
    IClock clock,
    ITeamResourceService resourceService,
    ITeamResourceGoogleClient resourceClient) : IGoogleDriveSync
{
    public async Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleResourceType? resourceType = null,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var claims = await LoadClaimsAsync(folderId: null, ct);
        if (resourceType.HasValue)
            claims = claims.Where(c => c.Resources.Any(r => r.ResourceType == resourceType.Value)).ToList();
        var diffs = new List<ResourceSyncDiff>();

        foreach (var claim in claims)
        {
            diffs.Add(claim.IsCollision
                ? await BuildCollisionDiffAsync(claim)
                : await ReconcileClaimAsync(claim with { Source = syncSource }, action, ct));
        }

        if (action == SyncAction.Execute &&
            await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct) == SyncMode.AddAndRemove)
        {
            var teamInfos = await teams.GetTeamsAsync(ct);
            var succeeded = diffs.Where(d => d.ErrorMessage is null).Select(d => d.GoogleId).ToHashSet(StringComparer.Ordinal);
            var resources = claims.SelectMany(c => c.Resources)
                .Where(r => !resourceType.HasValue || r.ResourceType == resourceType.Value).ToList();
            foreach (var group in resources.GroupBy(r => r.TeamId))
            {
                if (teamInfos.TryGetValue(group.Key, out var team) && !team.IsActive &&
                    group.All(r => succeeded.Contains(r.GoogleId)))
                {
                    foreach (var type in group.Select(r => r.ResourceType).Distinct())
                        await resourceService.DeactivateResourcesForTeamAsync(group.Key, type, ct);
                }
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
            : await ReconcileClaimAsync(claim with { Source = syncSource }, action, ct);
    }

    /// <summary>
    /// Loads every source's claims, one <see cref="IGoogleDriveAccessSource"/>
    /// at a time so a throwing source is skipped without aborting the others.
    /// </summary>
    private async Task<IReadOnlyList<FolderClaim>> LoadClaimsAsync(string? folderId, CancellationToken ct)
    {
        var claims = new List<(string SourceName, string FolderId, Dictionary<Guid, DrivePermissionLevel> Access)>();

        foreach (var source in sources)
        {
            Dictionary<string, Dictionary<Guid, DrivePermissionLevel>> expected;
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

        var resources = (await resourceRepository.GetActiveByResourceTypeAsync(GoogleResourceType.DriveFolder, ct))
            .Concat(await resourceRepository.GetActiveByResourceTypeAsync(GoogleResourceType.DriveFile, ct))
            .ToLookup(r => r.GoogleId, StringComparer.Ordinal);

        return claims
            .GroupBy(c => c.FolderId, StringComparer.Ordinal)
            .Select(g => new FolderClaim(
                g.Key,
                g.Count(),
                g.Select(c => c.SourceName).Distinct(StringComparer.Ordinal).ToArray(),
                g.First().Access, resources[g.Key].ToList()))
            .ToList();
    }

    private async Task<ResourceSyncDiff> ReconcileClaimAsync(
        FolderClaim claim,
        SyncAction action,
        CancellationToken ct)
    {
        try
        {
            var expectedMembers = await HydrateExpectedMembersAsync(claim.Access, ct);

            var permsResult = await drivePermissions.ListPermissionsAsync(claim.FolderId, ct);
            if (permsResult.Permissions is null)
            {
                var error = $"Google Drive permission list failed (HTTP {permsResult.Error?.StatusCode}): {permsResult.Error?.RawMessage}";
                logger.LogWarning("Google Drive access sync failed for {FolderId}: {Error}", claim.FolderId, error);
                if (action == SyncAction.Execute && claim.Resources.Count > 0)
                    await resourceRepository.SetErrorMessageManyAsync(claim.Resources.Select(r => r.Id).ToList(), error, ct);
                return BuildErrorDiff(claim.FolderId, error, claim.Primary);
            }

            var serviceAccountEmail = await resourceClient.GetServiceAccountEmailAsync(ct);
            var plan = BuildPlan(expectedMembers, permsResult.Permissions, serviceAccountEmail);
            var identities = await ResolveExtraEmailIdentitiesAsync(
                plan.Members.Where(m => m.UserId is null && m.State is MemberSyncState.Extra or MemberSyncState.Inherited).Select(m => m.Email), ct);
            for (var i = 0; i < plan.Members.Count; i++)
            {
                var member = plan.Members[i];
                if (member.UserId is null && identities.TryGetValue(member.Email, out var identity))
                    plan.Members[i] = member with { DisplayName = identity.DisplayName, UserId = identity.UserId, ProfilePictureUrl = identity.ProfilePictureUrl };
            }

            if (action == SyncAction.Execute)
            {
                var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct);
                if (mode != SyncMode.None)
                {
                    await ApplyMissingAndChangedAsync(claim, plan, mode, ct);

                    if (mode == SyncMode.AddAndRemove)
                        await ApplyExtraAsync(claim, plan, ct);
                }
            }

            if (action == SyncAction.Execute && claim.Resources.Count > 0)
                await resourceRepository.MarkSyncedManyAsync(claim.Resources.Select(r => r.Id).ToList(), clock.GetCurrentInstant(), ct);

            return new ResourceSyncDiff
            {
                ResourceId = claim.Primary?.Id ?? Guid.Empty,
                ResourceName = claim.Primary?.Name ?? claim.FolderId,
                ResourceType = (claim.Primary?.ResourceType ?? GoogleResourceType.DriveFolder).ToString(),
                GoogleId = claim.FolderId,
                Url = claim.Primary?.Url,
                PermissionLevel = claim.Primary?.DrivePermissionLevel.ToString(),
                Members = plan.Members
            };
        }
        catch (Exception ex) when (claim.Resources.Count > 0 && ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error reconciling Drive resource {GoogleId}", claim.FolderId);
            if (action == SyncAction.Execute && claim.Resources.Count > 0)
                await resourceRepository.SetErrorMessageManyAsync(claim.Resources.Select(r => r.Id).ToList(), ex.Message, ct);
            return BuildErrorDiff(claim.FolderId, ex.Message, claim.Primary);
        }
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
            // canonical form (#945), same as the Teams-keyed Drive path.
            var canonicalEmail = EmailNormalization.CanonicalizeGmail(email);
            result[userId] = new DriveExpectedMember(userId, canonicalEmail, user.BurnerName, user.ProfilePictureUrl, level);
        }

        return result;
    }

    private static DrivePlan BuildPlan(
        IReadOnlyDictionary<Guid, DriveExpectedMember> expectedMembers,
        IReadOnlyList<DrivePermission> permissions,
        string? serviceAccountEmail)
    {
        var byEmail = expectedMembers.Values
            .Where(m => !NormalizingEmailComparer.Instance.Equals(m.Email, serviceAccountEmail))
            .GroupBy(m => m.Email, NormalizingEmailComparer.Instance)
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
            if (NormalizingEmailComparer.Instance.Equals(email, serviceAccountEmail))
                continue;
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
            {
                members.Add(new MemberSyncStatus(email, email, MemberSyncState.Inherited, [], extraRole, inheritedRole));
                continue;
            }
            members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, [], extraRole, inheritedRole));
        }

        foreach (var email in allEmails.Where(email => !byEmail.ContainsKey(email) && !directEmails.Contains(email)))
        {
            roleByEmail.TryGetValue(email, out var inheritedRole);
            members.Add(new MemberSyncStatus(email, email, MemberSyncState.Inherited, [], inheritedRole));
        }

        return new DrivePlan(members, idByEmail);
    }

    private async Task<Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>>
        ResolveExtraEmailIdentitiesAsync(IEnumerable<string> emails, CancellationToken cancellationToken)
    {
        var emailList = emails.ToList();
        if (emailList.Count == 0)
            return new Dictionary<string, (string, Guid, string?)>(GmailAliasEmailComparer.Instance);

        // One winner per address — an unverified duplicate must not outrank the real owner,
        // since this id is what the sync log attributes the row to.
        var owners = UserEmailMatchOwner.ByEmail(
            await userEmailService.MatchByEmailsAsync(emailList, cancellationToken));
        var userIds = owners.Values.Select(m => m.UserId).Distinct().ToList();
        var usersById = await userService.GetUserInfosAsync(userIds, cancellationToken);

        var result = new Dictionary<string, (string DisplayName, Guid UserId, string? ProfilePictureUrl)>(
            GmailAliasEmailComparer.Instance);

        foreach (var (email, match) in owners)
        {
            if (usersById.TryGetValue(match.UserId, out var user))
            {
                result.TryAdd(email, (user.BurnerName, match.UserId, user.ProfilePictureUrl));
            }
        }

        return result;
    }

    private async Task ApplyMissingAndChangedAsync(FolderClaim claim, DrivePlan plan, SyncMode mode, CancellationToken ct)
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
                await UpdateAndLogAsync(claim, member, permissionId, ct);
                continue;
            }

            var role = member.ExpectedRole!;
            var result = await drivePermissions.CreatePermissionAsync(claim.FolderId, member.Email, role, ct);
            switch (result.Outcome)
            {
                case DrivePermissionCreateOutcome.Created:
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.Primary?.Id ?? Guid.Empty,
                        $"Granted Drive access ({role}) to {member.Email} ({claim.FolderId})",
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, claim.Source, success: true,
                        userId: member.UserId, ct: ct);
                    break;
                case DrivePermissionCreateOutcome.AlreadyExists:
                    logger.LogDebug("Permission already exists for {Email} on {FolderId}", member.Email, claim.FolderId);
                    break;
                case DrivePermissionCreateOutcome.Failed:
                    var error = $"Google Drive add failed for {member.Email} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                    logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, member.Email, error);
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.Primary?.Id ?? Guid.Empty, error,
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, claim.Source, success: false,
                        errorMessage: error, userId: member.UserId, ct: ct);
                    if (member.UserId is { } userId)
                        await HandleDriveAddFailureAsync(userId, claim.FolderId, member.Email, result.Error, ct);
                    break;
            }
        }
    }

    private async Task HandleDriveAddFailureAsync(
        Guid userId,
        string folderId,
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

        if (!await userService.TrySetGoogleEmailStatusFromSyncAsync(userId, GoogleEmailStatus.Rejected, ct))
            return;

        logger.LogWarning(
            "Google rejected target email {Email} while granting Drive permission on {GoogleId} - HTTP 400. " +
            "Google email status marked Rejected for the address. Google error: {ErrorMessage}",
            userEmail,
            folderId,
            rawMessage);
    }

    /// <summary>Drive-specific no-Google-account detector. Generic phrases excluded — they overlap with sharing-policy errors (#677).</summary>
    private static bool IsDriveTargetRejection(string rawMessage)
        => rawMessage.Contains("does not have a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("no google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not associated with a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("sendnotificationemail", StringComparison.OrdinalIgnoreCase);

    private async Task ApplyExtraAsync(FolderClaim claim, DrivePlan plan, CancellationToken ct)
    {
        foreach (var member in plan.Members.Where(m => m.State == MemberSyncState.Extra))
        {
            if (!plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                continue;

            if (member.ExpectedRole is not null)
            {
                // This is a mixed permission: only the direct elevation is extra.
                // Preserve the inherited floor and do not claim all access was removed.
                await UpdateAndLogAsync(claim, member, permissionId, ct);
                continue;
            }

            // Telling someone their access was removed when the delete failed (or the
            // permission turned out to be inherited and untouchable) is a false notice.
            if (await DeleteAndLogAsync(claim, member.Email, member.UserId, permissionId, ct))
                await NotifyRemovalAsync(member.Email, claim, ct);
        }
    }

    private async Task UpdateAndLogAsync(FolderClaim claim, MemberSyncStatus member, string permissionId, CancellationToken ct)
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
            action, claim.Primary?.Id ?? Guid.Empty, description, nameof(GoogleDriveAccessSyncService),
            member.Email, role, claim.Source, success: error is null,
            errorMessage: error is null ? null : description, userId: member.UserId, ct: ct);
    }

    /// <summary>Deletes one permission and logs the outcome. True only when Drive actually removed it.</summary>
    private async Task<bool> DeleteAndLogAsync(FolderClaim claim, string email, Guid? userId, string permissionId, CancellationToken ct)
    {
        var result = await drivePermissions.DeletePermissionAsync(claim.FolderId, permissionId, ct);
        switch (result.Outcome)
        {
            case DrivePermissionDeleteOutcome.Deleted:
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, claim.Primary?.Id ?? Guid.Empty,
                    $"Removed {email} from Drive folder {claim.FolderId}",
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", claim.Source, success: true,
                    userId: userId, ct: ct);
                return true;
            case DrivePermissionDeleteOutcome.InheritedPermission:
                logger.LogWarning(
                    "Skipping removal of {Email} from {FolderId} — permission is inherited, not direct",
                    email, claim.FolderId);
                break;
            case DrivePermissionDeleteOutcome.Failed:
                var error = $"Google Drive remove failed for {email} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, email, error);
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, claim.Primary?.Id ?? Guid.Empty, error,
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", claim.Source, success: false,
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
                email, claim.Primary?.ResourceType ?? GoogleResourceType.DriveFolder,
                claim.Primary?.Name, claim.Primary?.Url ?? claim.FolderId,
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

        return BuildErrorDiff(claim.FolderId, error, claim.Primary);
    }

    private static ResourceSyncDiff BuildErrorDiff(string folderId, string error, GoogleResource? resource = null) => new()
    {
        ResourceId = resource?.Id ?? Guid.Empty,
        ResourceName = resource?.Name ?? folderId,
        ResourceType = (resource?.ResourceType ?? GoogleResourceType.DriveFolder).ToString(),
        GoogleId = folderId,
        Url = resource?.Url,
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

    private sealed record FolderClaim(string FolderId, int ClaimCount, string[] SourceNames,
        Dictionary<Guid, DrivePermissionLevel> Access, IReadOnlyList<GoogleResource> Resources,
        GoogleSyncSource Source = GoogleSyncSource.ScheduledSync)
    {
        public bool IsCollision => ClaimCount > 1;
        public GoogleResource? Primary => Resources.FirstOrDefault();
    }

    private sealed record DriveExpectedMember(Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, DrivePermissionLevel Level);

    private sealed record DrivePlan(List<MemberSyncStatus> Members, Dictionary<string, string> PermissionIdByEmail);
}
