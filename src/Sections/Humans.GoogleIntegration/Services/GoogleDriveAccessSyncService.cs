using Humans.GoogleIntegration.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

/// <summary>Reconciles Drive resources claimed by Teams and Workgroups through their public sources.</summary>
internal sealed class GoogleDriveAccessSyncService(
    IEnumerable<IGoogleDriveAccessSource> sources,
    IGoogleDrivePermissionsClient drivePermissions,
    IUserService userService,
    IWorkspaceIdentityManagement workspaceIdentities,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleRemovalNotificationService removalNotifications,
    IGoogleResourceRepository resourceRepository,
    ITeamService teamService,
    ITeamResourceManagement teamResourceService,
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
        var load = await LoadClaimsAsync(sources, folderId: null, logger, ct);
        var claims = load.Claims.Select(claim => claim with
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
            diffs.Add(await ReconcileClaimSafelyAsync(await WithPathAsync(claim, ct), action, syncSource, ct));
        }

        if (load.Partial)
            diffs.AddRange(await FlagUnclaimedResourcesAsync(
                resources, claims, resourceType, action, ct));

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
        var load = await LoadClaimsAsync(sources, folderId, logger, ct);
        var claim = load.Claims.SingleOrDefault(c =>
            string.Equals(c.FolderId, folderId, StringComparison.Ordinal));

        if (claim is null)
        {
            var error = load.Partial
                ? SourceFailedMessage
                : "No Google Drive access source claims this folder";
            logger.LogWarning("Google Drive access sync: no source claims folder id {FolderId}", folderId);
            return BuildDiff(new FolderClaim(folderId, new([]),
                resources.Where(r => string.Equals(r.GoogleId, folderId, StringComparison.Ordinal)).ToList()), [], error);
        }

        claim = claim with
        {
            Resources = resources.Where(r => string.Equals(r.GoogleId, folderId, StringComparison.Ordinal)).ToList()
        };
        return await ReconcileClaimSafelyAsync(await WithPathAsync(claim, ct), action, syncSource, ct);
    }

    /// <summary>
    /// A registered resource already carries its full path as its name; a folder no section
    /// registered (a Workgroup's) gets the same path from Drive so nobody is shown a raw file id.
    /// </summary>
    private async Task<FolderClaim> WithPathAsync(FolderClaim claim, CancellationToken ct)
    {
        if (claim.Resources.Count > 0) return claim;
        try
        {
            return claim with { Path = await DriveFolderPath.ResolveAsync(drivePermissions, claim.FolderId, logger, ct) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve the Drive path for {FolderId}; falling back to the source's name", claim.FolderId);
            return claim;
        }
    }

    /// <summary>
    /// Loads every source's claims, one <see cref="IGoogleDriveAccessSource"/>
    /// at a time so a throwing source is skipped without aborting the others.
    /// A folder claimed by several sources gets the union of their claims, highest
    /// level per user. When a source throws, every claim is marked partial so the
    /// run grants but never removes or demotes on an incomplete roster, and
    /// <see cref="ClaimLoad.Partial"/> is set even if no claim survived. With
    /// <paramref name="rethrowSourceFailures"/> the first failure propagates instead,
    /// for callers (the join-time grant) that must retry rather than act on a partial roster.
    /// </summary>
    internal static async Task<ClaimLoad> LoadClaimsAsync(
        IEnumerable<IGoogleDriveAccessSource> sources, string? folderId, ILogger logger, CancellationToken ct,
        bool rethrowSourceFailures = false)
    {
        var claims = new List<(string FolderId, GoogleDriveAccessClaim Claim)>();
        var partial = false;

        foreach (var source in sources)
        {
            Dictionary<string, GoogleDriveAccessClaim> expected;
            try
            {
                expected = await source.GetExpectedAccessAsync(folderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !rethrowSourceFailures)
            {
                logger.LogError(ex,
                    "Google Drive access source {SourceType} threw while computing expected access; skipping this source",
                    source.GetType().Name);
                partial = true;
                continue;
            }

            foreach (var (key, access) in expected)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                claims.Add((key.Trim(), access));
            }
        }

        return new ClaimLoad(claims
            .GroupBy(c => c.FolderId, StringComparer.Ordinal)
            .Select(g => new FolderClaim(g.Key, Union(g.Select(c => c.Claim).ToList()), [], partial))
            .ToList(), partial);
    }

    private const string SourceFailedMessage =
        "A Google Drive access source failed this run: its roster is incomplete and removals and demotions are skipped";

    /// <summary>
    /// A failed source takes with it the folders only it knew, so those resources get no claim and
    /// would otherwise be skipped without a trace. Surface each as an error diff (and on the row).
    /// </summary>
    private async Task<List<ResourceSyncDiff>> FlagUnclaimedResourcesAsync(
        IReadOnlyList<GoogleResource> resources, IReadOnlyList<FolderClaim> claims,
        GoogleResourceType? resourceType, SyncAction action, CancellationToken ct)
    {
        var claimed = claims.Select(c => c.FolderId).ToHashSet(StringComparer.Ordinal);
        var diffs = new List<ResourceSyncDiff>();
        foreach (var group in resources
            .Where(r => (resourceType is null || r.ResourceType == resourceType) && !claimed.Contains(r.GoogleId))
            .GroupBy(r => r.GoogleId, StringComparer.Ordinal))
        {
            var claim = new FolderClaim(group.Key, new([]), group.ToList(), Partial: true);
            diffs.Add(BuildDiff(claim, [], SourceFailedMessage));
            if (action == SyncAction.Execute)
                await resourceRepository.SetErrorMessageManyAsync(
                    claim.Resources.Select(r => r.Id).ToList(), SourceFailedMessage, ct);
        }

        return diffs;
    }

    private static GoogleDriveAccessClaim Union(IReadOnlyList<GoogleDriveAccessClaim> claims)
    {
        if (claims.Count == 1)
            return claims[0];

        var access = new Dictionary<Guid, DrivePermissionLevel>();
        var memberLinks = new Dictionary<Guid, IReadOnlyList<TeamLink>>();
        foreach (var claim in claims)
        {
            foreach (var (userId, level) in claim.Access)
                access[userId] = access.TryGetValue(userId, out var held) && held > level ? held : level;
            foreach (var (userId, links) in claim.MemberTeamLinks ?? new Dictionary<Guid, IReadOnlyList<TeamLink>>())
                memberLinks[userId] = memberLinks.TryGetValue(userId, out var known) ? [.. known, .. links] : links;
        }

        return new GoogleDriveAccessClaim(access, claims.SelectMany(c => c.LinkedTeams ?? []).ToList(), memberLinks,
            claims.Select(c => c.ResourceName).FirstOrDefault(n => n is not null));
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
            diff = await ReconcileClaimAsync(claim, action, syncSource, ct);
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
            if (member.State != MemberSyncState.Extra || !owners.TryGetValue(member.Email, out var owner))
                continue;
            members[i] = users.TryGetValue(owner.UserId, out var user)
                ? member with { UserId = owner.UserId, DisplayName = user.BurnerName, ProfilePictureUrl = user.ProfilePictureUrl }
                : member with { UserId = owner.UserId };
        }
    }

    private static ResourceSyncDiff BuildDiff(FolderClaim claim, List<MemberSyncStatus> members, string? error = null)
    {
        var primary = claim.Resources.FirstOrDefault();
        return new ResourceSyncDiff
        {
            ResourceId = primary?.Id ?? Guid.Empty,
            ResourceName = claim.DisplayName,
            ResourceType = (primary?.ResourceType ?? GoogleResourceType.DriveFolder).ToString(),
            GoogleId = claim.FolderId,
            Url = primary?.Url,
            ErrorMessage = error,
            PermissionLevel = primary is { DrivePermissionLevel: not DrivePermissionLevel.None }
                ? primary.DrivePermissionLevel.ToString() : null,
            LinkedTeams = claim.Claim.LinkedTeams?.ToList() ?? [],
            Members = members
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

                // A failed source may have claimed this folder too; its members would read as
                // extra or over-privileged, so hold removals and demotions until it answers.
                if (mode == SyncMode.AddAndRemove && !claim.Partial)
                    errors.AddRange(await ApplyExtraAsync(claim, plan, syncSource, ct));
            }
        }

        // Recorded in every mode and for previews: the roster is incomplete, so the folder is
        // neither "synced" nor trustworthy drift.
        if (claim.Partial)
            errors.Add(SourceFailedMessage);

        return BuildDiff(claim, plan.Members, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private async Task<Dictionary<Guid, DriveExpectedMember>> HydrateExpectedMembersAsync(
        IReadOnlyDictionary<Guid, DrivePermissionLevel> access,
        CancellationToken ct)
    {
        var eligibleMembers = await GoogleSyncMemberHydration.LoadAsync(
            access.Keys.ToList(), userService, workspaceIdentities, ct);
        var result = new Dictionary<Guid, DriveExpectedMember>();
        foreach (var (userId, level) in access)
        {
            if (level != DrivePermissionLevel.None && eligibleMembers.TryGetValue(userId, out var member))
                result[userId] = new DriveExpectedMember(
                    member.UserId, member.Email, member.DisplayName, member.ProfilePictureUrl, level, member.IdentityEmail);
        }

        return result;
    }

    private static DrivePlan BuildPlan(
        IReadOnlyDictionary<Guid, DriveExpectedMember> expectedMembers,
        IReadOnlyList<DrivePermission> permissions)
    {
        // Distinct member IDs may carry aliases that Google resolves to one address.
        // Reconcile that address once at the highest level already claimed by a source.
        var byEmail = expectedMembers.Values
            .GroupBy(member => member.Email, GmailAliasEmailComparer.Instance)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(member => member.Level)
                    .ThenBy(member => member.UserId).First(),
                GmailAliasEmailComparer.Instance);

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
                        : DrivePermissionRoleMapper.Parse(currentRole) > DrivePermissionRoleMapper.Parse(expectedRole)
                            ? MemberSyncState.Downgrade
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

        return new DrivePlan(members, idByEmail,
            byEmail.ToDictionary(kv => kv.Key, kv => kv.Value.IdentityEmail, GmailAliasEmailComparer.Instance));
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
            var result = await WithTransientRetryAsync(
                () => drivePermissions.CreatePermissionAsync(claim.FolderId, member.Email, role, ct), r => r.Error, ct);
            switch (result.Outcome)
            {
                case DrivePermissionCreateOutcome.Created:
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.ResourceId,
                        $"Granted Drive access ({role}) to {member.Email} ({claim.DisplayName})",
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, syncSource, success: true,
                        userId: member.UserId, ct: ct);
                    break;
                case DrivePermissionCreateOutcome.AlreadyExists:
                    logger.LogDebug("Permission already exists for {Email} on {FolderId}", member.Email, claim.FolderId);
                    break;
                case DrivePermissionCreateOutcome.Failed:
                    var error = $"Google Drive add failed for {MaskEmail(member.Email)} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                    logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, member.Email, error);
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted, claim.ResourceId, error,
                        nameof(GoogleDriveAccessSyncService),
                        member.Email, role, syncSource, success: false,
                        errorMessage: error, userId: member.UserId, ct: ct);
                    errors.Add(error);
                    if (result.Error is { IsDriveTargetRejection: true } && member.UserId is { } rejectedUserId)
                        await workspaceIdentities.SetStatusFromSyncAsync(
                            rejectedUserId, plan.IdentityEmailsByEmail[member.Email], GoogleEmailStatus.Rejected, ct);
                    break;
            }
        }
        return errors;
    }

    /// <summary>Waits between transient-failure attempts; tests replace it to skip the backoff.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private static readonly TimeSpan[] TransientRetryBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// Re-issues a Drive mutation while Google reports a transient error, so a one-off Google hiccup
    /// does not leave a member without access until the next pass (peterdrier/Humans#2037).
    /// Permanent failures, including target rejections, return on the first attempt.
    /// </summary>
    private async Task<T> WithTransientRetryAsync<T>(Func<Task<T>> call, Func<T, GoogleClientError?> errorOf, CancellationToken ct)
    {
        var result = await call();
        foreach (var backoff in TransientRetryBackoff)
        {
            if (errorOf(result) is not { IsTransient: true } error)
                break;
            logger.LogInformation("Transient Google Drive error (HTTP {StatusCode}), retrying in {Backoff}: {Error}",
                error.StatusCode, backoff, error.RawMessage);
            await Delay(backoff, ct);
            result = await call();
        }
        return result;
    }

    /// <summary>Keeps two characters of the local part, like the log pipeline's email redaction.</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[..Math.Min(2, at)]}***{email[at..]}";
    }

    private async Task<List<string>> ApplyExtraAsync(FolderClaim claim, DrivePlan plan, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var member in plan.Members.Where(m => m.State is MemberSyncState.Extra or MemberSyncState.Downgrade))
        {
            if (!plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                continue;

            if (member.ExpectedRole is not null)
            {
                // A downgrade, or a mixed permission whose direct elevation is extra: lower
                // it to the expected role and do not claim all access was removed.
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
        var error = await WithTransientRetryAsync(
            () => drivePermissions.UpdatePermissionAsync(claim.FolderId, permissionId, role, ct), e => e, ct);
        var action = DrivePermissionRoleMapper.Parse(member.CurrentRole) > DrivePermissionRoleMapper.Parse(role)
            ? GoogleSyncLogAction.AccessRevoked
            : GoogleSyncLogAction.AccessGranted;
        var description = error is null
            ? $"Changed Drive access for {member.Email} from {member.CurrentRole} to {role} ({claim.DisplayName})"
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
                    $"Removed {email} from Drive folder {claim.DisplayName}",
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
                claim.DisplayName, claim.Resources.FirstOrDefault()?.Url ?? claim.FolderId,
                SyncRemovalReason.Reconciliation, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue Drive removal notification for {Email} from {FolderId}", email, claim.FolderId);
        }
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

    internal sealed record ClaimLoad(IReadOnlyList<FolderClaim> Claims, bool Partial);

    internal sealed record FolderClaim(string FolderId,
        GoogleDriveAccessClaim Claim, IReadOnlyList<GoogleResource> Resources, bool Partial = false, string? Path = null)
    {
        public Dictionary<Guid, DrivePermissionLevel> Access => Claim.Access;
        public Guid ResourceId => Resources.FirstOrDefault()?.Id ?? Guid.Empty;

        /// <summary>Registered resource name (its full path), else the path Drive gave, else the source's name, else the raw file id.</summary>
        public string DisplayName => Resources.FirstOrDefault()?.Name ?? Path ?? Claim.ResourceName ?? FolderId;
    }

    private sealed record DriveExpectedMember(
        Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl, DrivePermissionLevel Level, string IdentityEmail);

    private sealed record DrivePlan(
        List<MemberSyncStatus> Members, Dictionary<string, string> PermissionIdByEmail,
        Dictionary<string, string> IdentityEmailsByEmail);
}
