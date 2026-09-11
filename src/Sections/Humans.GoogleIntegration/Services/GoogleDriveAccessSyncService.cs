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
/// Mirrors <see cref="GoogleGroupSyncService"/> exactly, for Drive folders
/// instead of Groups. The Teams-keyed <c>google_resources</c> Drive path
/// (<see cref="GoogleWorkspaceSyncService"/>) is untouched by this class.
/// </summary>
internal sealed class GoogleDriveAccessSyncService(
    IEnumerable<IGoogleDriveAccessSource> sources,
    IGoogleDrivePermissionsClient drivePermissions,
    IUserServiceRead userService,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleRemovalNotificationService removalNotifications,
    ILogger<GoogleDriveAccessSyncService> logger) : IGoogleDriveSync
{
    public async Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default)
    {
        var claims = await LoadClaimsAsync(folderId: null, ct);
        var diffs = new List<ResourceSyncDiff>();

        foreach (var claim in claims)
        {
            diffs.Add(claim.IsCollision
                ? await BuildCollisionDiffAsync(claim)
                : await ReconcileClaimAsync(claim, action, ct));
        }

        return new SyncPreviewResult { Diffs = diffs };
    }

    public async Task<ResourceSyncDiff> ReconcileOneAsync(
        string folderId,
        SyncAction action,
        CancellationToken ct = default)
    {
        var claims = await LoadClaimsAsync(folderId, ct);
        var claim = claims.SingleOrDefault(c =>
            string.Equals(c.FolderId, folderId, StringComparison.OrdinalIgnoreCase));

        if (claim is null)
        {
            const string error = "No Google Drive access source claims this folder";
            logger.LogWarning("Google Drive access sync: no source claims folder id {FolderId}", folderId);
            return BuildErrorDiff(folderId, error);
        }

        return claim.IsCollision
            ? await BuildCollisionDiffAsync(claim)
            : await ReconcileClaimAsync(claim, action, ct);
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

        return claims
            .GroupBy(c => c.FolderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FolderClaim(
                g.Key,
                g.Count(),
                g.Select(c => c.SourceName).Distinct(StringComparer.Ordinal).ToArray(),
                g.First().Access))
            .ToList();
    }

    private async Task<ResourceSyncDiff> ReconcileClaimAsync(
        FolderClaim claim,
        SyncAction action,
        CancellationToken ct)
    {
        var expectedMembers = await HydrateExpectedMembersAsync(claim.Access, ct);

        var permsResult = await drivePermissions.ListPermissionsAsync(claim.FolderId, ct);
        if (permsResult.Permissions is null)
        {
            var error = $"Google Drive permission list failed (HTTP {permsResult.Error?.StatusCode}): {permsResult.Error?.RawMessage}";
            logger.LogWarning("Google Drive access sync failed for {FolderId}: {Error}", claim.FolderId, error);
            return BuildErrorDiff(claim.FolderId, error);
        }

        var plan = BuildPlan(expectedMembers, permsResult.Permissions);

        if (action == SyncAction.Execute)
        {
            var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, ct);
            if (mode != SyncMode.None)
            {
                await ApplyMissingAndChangedAsync(claim, plan, ct);

                if (mode == SyncMode.AddAndRemove)
                    await ApplyExtraAsync(claim, plan, ct);
            }
        }

        return new ResourceSyncDiff
        {
            ResourceId = Guid.Empty,
            ResourceName = claim.FolderId,
            ResourceType = GoogleResourceType.DriveFolder.ToString(),
            GoogleId = claim.FolderId,
            Members = plan.Members
        };
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
            if (user.GoogleEmailStatus == GoogleEmailStatus.Rejected || user.IsDeletionPending || user.MergedToUserId is not null)
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
        IReadOnlyList<DrivePermission> permissions)
    {
        var byEmail = expectedMembers.Values.ToDictionary(m => m.Email, NormalizingEmailComparer.Instance);

        var allEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
        var directEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
        var roleByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
        var idByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);

        foreach (var perm in permissions)
        {
            if (!IsAnyUserPermission(perm))
                continue;

            var email = EmailNormalization.CanonicalizeGmail(perm.EmailAddress!);
            allEmails.Add(email);
            if (!string.IsNullOrEmpty(perm.Role))
                roleByEmail[email] = perm.Role;
            if (IsDirectManagedPermission(perm) && perm.Id is not null)
            {
                directEmails.Add(email);
                idByEmail[email] = perm.Id;
            }
        }

        var members = new List<MemberSyncStatus>();
        foreach (var member in expectedMembers.Values)
        {
            roleByEmail.TryGetValue(member.Email, out var currentRole);
            var expectedRole = member.Level.ToApiRole();

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
                state = ParseApiRole(currentRole) is { } inherited && inherited < member.Level
                    ? MemberSyncState.Missing
                    : MemberSyncState.Inherited;
            else
                state = string.Equals(currentRole, expectedRole, StringComparison.Ordinal)
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
            members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, [], extraRole));
        }

        return new DrivePlan(members, idByEmail);
    }

    private async Task ApplyMissingAndChangedAsync(FolderClaim claim, DrivePlan plan, CancellationToken ct)
    {
        foreach (var member in plan.Members.Where(m => m.State is MemberSyncState.Missing or MemberSyncState.WrongRole))
        {
            // A role change has no direct "update" — the permission is
            // removed and re-created at the new level (Drive's
            // permissions.create is idempotent, not an upsert-by-role).
            if (member.State == MemberSyncState.WrongRole && plan.PermissionIdByEmail.TryGetValue(member.Email, out var oldPermissionId))
            {
                await DeleteAndLogAsync(claim, member.Email, member.UserId, oldPermissionId, ct);
            }

            var role = member.ExpectedRole!;
            var result = await drivePermissions.CreatePermissionAsync(claim.FolderId, member.Email, role, ct);
            switch (result.Outcome)
            {
                case DrivePermissionCreateOutcome.Created:
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, Guid.Empty,
                        $"Granted Drive access ({role}) to {member.Email} ({claim.FolderId})",
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, GoogleSyncSource.ScheduledSync, success: true,
                        userId: member.UserId, ct: ct);
                    break;
                case DrivePermissionCreateOutcome.AlreadyExists:
                    logger.LogDebug("Permission already exists for {Email} on {FolderId}", member.Email, claim.FolderId);
                    break;
                case DrivePermissionCreateOutcome.Failed:
                    var error = $"Google Drive add failed for {member.Email} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                    logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, member.Email, error);
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, Guid.Empty, error,
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, GoogleSyncSource.ScheduledSync, success: false,
                        errorMessage: error, userId: member.UserId, ct: ct);
                    break;
            }
        }
    }

    private async Task ApplyExtraAsync(FolderClaim claim, DrivePlan plan, CancellationToken ct)
    {
        foreach (var member in plan.Members.Where(m => m.State == MemberSyncState.Extra))
        {
            if (!plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                continue;

            // Telling someone their access was removed when the delete failed (or the
            // permission turned out to be inherited and untouchable) is a false notice.
            if (await DeleteAndLogAsync(claim, member.Email, member.UserId, permissionId, ct))
                await NotifyRemovalAsync(member.Email, claim.FolderId, ct);
        }
    }

    /// <summary>Deletes one permission and logs the outcome. True only when Drive actually removed it.</summary>
    private async Task<bool> DeleteAndLogAsync(FolderClaim claim, string email, Guid? userId, string permissionId, CancellationToken ct)
    {
        var result = await drivePermissions.DeletePermissionAsync(claim.FolderId, permissionId, ct);
        switch (result.Outcome)
        {
            case DrivePermissionDeleteOutcome.Deleted:
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked, Guid.Empty,
                    $"Removed {email} from Drive folder {claim.FolderId}",
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", GoogleSyncSource.ScheduledSync, success: true,
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
                    GoogleSyncLogAction.AccessRevoked, Guid.Empty, error,
                    nameof(GoogleDriveAccessSyncService),
                    email, "MEMBER", GoogleSyncSource.ScheduledSync, success: false,
                    errorMessage: error, userId: userId, ct: ct);
                break;
        }

        return false;
    }

    private async Task NotifyRemovalAsync(string email, string folderId, CancellationToken ct)
    {
        try
        {
            await removalNotifications.NotifyRemovalAsync(
                email, GoogleResourceType.DriveFolder, resourceName: null, folderId,
                SyncRemovalReason.Reconciliation, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue Drive removal notification for {Email} from {FolderId}", email, folderId);
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

        return BuildErrorDiff(claim.FolderId, error);
    }

    private static ResourceSyncDiff BuildErrorDiff(string folderId, string error) => new()
    {
        ResourceId = Guid.Empty,
        ResourceName = folderId,
        ResourceType = GoogleResourceType.DriveFolder.ToString(),
        GoogleId = folderId,
        ErrorMessage = error
    };

    private static DrivePermissionLevel? ParseApiRole(string? role) => role switch
    {
        "reader" => DrivePermissionLevel.Viewer,
        "commenter" => DrivePermissionLevel.Commenter,
        "writer" => DrivePermissionLevel.Contributor,
        "fileOrganizer" => DrivePermissionLevel.ContentManager,
        "organizer" => DrivePermissionLevel.Manager,
        _ => null
    };

    private static bool IsAnyUserPermission(DrivePermission perm)
    {
        if (!string.Equals(perm.Type, "user", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(perm.EmailAddress))
            return false;
        return !perm.EmailAddress.EndsWith(".iam.gserviceaccount.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectManagedPermission(DrivePermission perm)
    {
        if (!IsAnyUserPermission(perm))
            return false;
        if (string.Equals(perm.Role, "owner", StringComparison.OrdinalIgnoreCase))
            return false;

        // #945: any inherited component makes a permission undeletable at
        // this level — exclude it from the managed set up front.
        return !perm.HasInheritedComponent;
    }

    private sealed record FolderClaim(string FolderId, int ClaimCount, string[] SourceNames, Dictionary<Guid, DrivePermissionLevel> Access)
    {
        public bool IsCollision => ClaimCount > 1;
    }

    private sealed record DriveExpectedMember(Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, DrivePermissionLevel Level);

    private sealed record DrivePlan(List<MemberSyncStatus> Members, Dictionary<string, string> PermissionIdByEmail);
}
