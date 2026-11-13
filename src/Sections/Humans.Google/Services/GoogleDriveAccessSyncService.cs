using Humans.Google.Contracts;
using static Humans.Google.Services.DrivePermissionPlanning;
using static Humans.Google.Services.DriveAccessClaims;
using Humans.Users.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.Google.Services.Workspace;
using Humans.Google.Data;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Google.Services;

/// <summary>Reconciles Drive resources claimed by Teams and Workgroups through their public sources.</summary>
internal sealed class GoogleDriveAccessSyncService(
    IEnumerable<IGoogleDriveAccessSource> sources,
    IGoogleDrivePermissionsClient drivePermissions,
    IUserService userService,
    IWorkspaceIdentityManagement workspaceIdentities,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleSyncLogViewer googleSyncLogViewer,
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
        var passStart = clock.GetCurrentInstant();
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
            ct.ThrowIfCancellationRequested();
            diffs.Add(await ReconcileClaimSafelyAsync(await WithPathAsync(claim, ct), action, syncSource, passStart, ct));
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
        var passStart = clock.GetCurrentInstant();
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
        return await ReconcileClaimSafelyAsync(await WithPathAsync(claim, ct), action, syncSource, passStart, ct);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not resolve the Drive path for {FolderId}; falling back to the source's name", claim.FolderId);
            return claim;
        }
    }

    private const string SourceFailedMessage =
        "A Google Drive access source failed this run: its roster is incomplete and removals and demotions are skipped";

    /// <summary>
    /// A failed source takes with it the folders only it knew, so those resources get no claim and
    /// would otherwise be skipped without a trace. Surface each as an error diff (and on the row).
    /// </summary>
    private async Task<List<ResourceSyncDiff>> FlagUnclaimedResourcesAsync(
        IReadOnlyList<TeamGoogleResource> resources, IReadOnlyList<FolderClaim> claims,
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

    private async Task<IReadOnlyList<TeamGoogleResource>> LoadResourcesAsync(CancellationToken ct)
    {
        var resources = new List<TeamGoogleResource>();
        foreach (var type in new[] { GoogleResourceType.DriveFolder, GoogleResourceType.DriveFile, GoogleResourceType.SharedDrive })
            resources.AddRange(await resourceRepository.GetActiveByResourceTypeAsync(type, ct));
        return resources;
    }

    private async Task<ResourceSyncDiff> ReconcileClaimSafelyAsync(
        FolderClaim claim, SyncAction action, GoogleSyncSource syncSource, Instant passStart, CancellationToken ct)
    {
        ResourceSyncDiff diff;
        try
        {
            diff = await ReconcileClaimAsync(claim, action, syncSource, passStart, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error syncing Drive resource {GoogleId}", claim.FolderId);
            diff = BuildDiff(claim, [], ex.Message);
        }

        if (action == SyncAction.Execute && claim.Resources.Count > 0)
        {
            try
            {
                // Finish recording the completed execution even if its last mutation observed shutdown.
                var commitCt = CancellationToken.None;
                var ids = claim.Resources.Select(r => r.Id).ToList();
                if (diff.ErrorMessage is not null)
                    await resourceRepository.SetErrorMessageManyAsync(ids, diff.ErrorMessage, commitCt);
                else if (await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, commitCt) != SyncMode.None)
                    await resourceRepository.MarkSyncedManyAsync(ids, clock.GetCurrentInstant(), commitCt);
            }
            catch (OperationCanceledException ex)
            {
                throw new InvalidOperationException("Failed to record the Drive reconciliation outcome.", ex);
            }
        }
        return diff;
    }

    private async Task DeactivateRetiredTeamResourcesAsync(
        IReadOnlyList<TeamGoogleResource> resources, IReadOnlyList<ResourceSyncDiff> diffs, CancellationToken ct)
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
        var owners = UserEmailMatchOwner.ByEmail(await userEmailService.MatchByEmailsAsync(extraEmails, ct))
            .ToDictionary(kv => kv.Key, kv => kv.Value.UserId, GmailAliasEmailComparer.Instance);
        // A plus-address fallback grant is held under a base address no user_emails row claims;
        // its recorded Drive address still attributes it.
        foreach (var (userId, drive) in await workspaceIdentities.GetDriveEmailsAsync(ct))
            owners.TryAdd(drive.DriveEmail, userId);
        var users = await userService.GetUserInfosAsync(owners.Values.Distinct().ToList(), ct);
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (member.State != MemberSyncState.Extra || !owners.TryGetValue(member.Email, out var ownerId))
                continue;
            members[i] = users.TryGetValue(ownerId, out var user)
                ? member with { UserId = ownerId, DisplayName = user.BurnerName, ProfilePictureUrl = user.ProfilePictureUrl }
                : member with { UserId = ownerId };
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
        Instant passStart,
        CancellationToken ct)
    {
        var expectedMembers = await HydrateExpectedMembersAsync(claim.Access, ct);

        var (permsResult, _) = await WithTransientRetryAsync(
            () => drivePermissions.ListPermissionsAsync(claim.FolderId, ct), r => r.Error, ct);
        if (permsResult.Permissions is null)
        {
            var error = $"Google Drive permission list failed (HTTP {permsResult.Error?.StatusCode}): {permsResult.Error?.RawMessage}";
            logger.LogWarning("Google Drive access sync failed for {FolderId}: {Error}", claim.FolderId, error);
            return BuildDiff(claim, [], error);
        }

        var plan = DrivePermissionPlanning.BuildPlan(expectedMembers, permsResult.Permissions);
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
                errors.AddRange(await ApplyMissingAndChangedAsync(claim, plan, mode, syncSource, passStart, ct));

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
        var driveEmails = await workspaceIdentities.GetDriveEmailsAsync(ct);
        var result = new Dictionary<Guid, DriveExpectedMember>();
        foreach (var (userId, level) in access)
        {
            if (level != DrivePermissionLevel.None && eligibleMembers.TryGetValue(userId, out var member))
                result[userId] = new DriveExpectedMember(
                    member.UserId,
                    DrivePlusAddressFallback.Apply(driveEmails, member.UserId, member.IdentityEmail, member.Email),
                    member.DisplayName, member.ProfilePictureUrl, level, member.IdentityEmail);
        }

        return result;
    }

    private async Task<List<string>> ApplyMissingAndChangedAsync(
        FolderClaim claim, DrivePlan plan, SyncMode mode, GoogleSyncSource syncSource, Instant passStart, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var member in plan.Members.Where(m => m.State is MemberSyncState.Missing or MemberSyncState.WrongRole))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var commitCt = CancellationToken.None;

                if (member.State == MemberSyncState.WrongRole && plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                {
                    // AddOnly can elevate access but cannot reduce it. Unknown roles also
                    // wait for AddAndRemove rather than assuming a change is an elevation.
                    if (mode == SyncMode.AddOnly &&
                        !(DrivePermissionRoleMapper.Parse(member.CurrentRole) < DrivePermissionRoleMapper.Parse(member.ExpectedRole)))
                        continue;
                    if (await UpdateAndLogAsync(claim, member, permissionId, syncSource, commitCt) is { } updateError) errors.Add(updateError);
                    continue;
                }

                var role = member.ExpectedRole!;
                var retried = false;
                async Task<DrivePermissionMutationResult> CreateAsync(string email)
                {
                    (var created, retried) = await WithTransientRetryAsync(
                        () => drivePermissions.CreatePermissionAsync(claim.FolderId, email, role, commitCt), r => r.Error, commitCt);
                    return created;
                }
                var (result, grantedEmail) = member.UserId is { } memberUserId
                    ? await DrivePlusAddressFallback.CreateAsync(
                        CreateAsync, member.Email, memberUserId, plan.IdentityEmailsByEmail[member.Email], workspaceIdentities, commitCt)
                    : (await CreateAsync(member.Email), member.Email);
                var insteadOf = string.Equals(grantedEmail, member.Email, StringComparison.Ordinal)
                    ? ""
                    : $" in place of {member.Email}, which Drive refused";
                switch (result.Outcome)
                {
                    // After a transient retry, AlreadyExists means the failed attempt was applied
                    // by Google anyway; the grant is still ours to record. So is a fallback onto a
                    // base address that already had access: the member's Drive address changed.
                    case DrivePermissionCreateOutcome.Created:
                    case DrivePermissionCreateOutcome.AlreadyExists when retried || insteadOf.Length > 0:
                        await googleSyncLog.LogAsync(
                            GoogleSyncLogAction.AccessGranted, claim.ResourceId,
                            $"Granted Drive access ({role}) to {grantedEmail}{insteadOf} ({claim.DisplayName})",
                            nameof(GoogleDriveAccessSyncService),
                            grantedEmail, role, syncSource, success: true,
                            userId: member.UserId, ct: commitCt);
                        break;
                    case DrivePermissionCreateOutcome.AlreadyExists:
                        logger.LogDebug("Permission already exists for {Email} on {FolderId}", grantedEmail, claim.FolderId);
                        break;
                    case DrivePermissionCreateOutcome.Failed:
                        var error = $"Google Drive add failed for {MaskEmail(member.Email)} (HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
                        logger.LogWarning("Google Drive access sync failed for {FolderId} member {Email}: {Error}", claim.FolderId, member.Email, error);
                        // An internal error is usually a hiccup, so one run proves nothing; the same refusal on the
                        // next run is how Drive turns away an address it will never accept.
                        var rejected = result.Error is { IsDriveTargetRejection: true }
                            || (result.Error is { IsInternalError: true } && member.UserId is { } failedUserId
                                && await LastGrantFailedWithInternalErrorAsync(claim, member.Email, failedUserId, passStart, commitCt));
                        var description = $"{error} ({claim.DisplayName})";
                        await googleSyncLog.LogAsync(
                            GoogleSyncLogAction.AccessGranted, claim.ResourceId, description,
                            nameof(GoogleDriveAccessSyncService),
                            member.Email, role, syncSource, success: false,
                            errorMessage: error, userId: member.UserId, ct: commitCt);
                        errors.Add(error);
                        if (rejected && member.UserId is { } rejectedUserId)
                            await workspaceIdentities.SetStatusFromSyncAsync(
                                rejectedUserId, plan.IdentityEmailsByEmail[member.Email], GoogleEmailStatus.Rejected, commitCt);
                        break;
                }
            }
            catch (OperationCanceledException ex)
            {
                // This unit uses None; its cancellation is a dependency fault, not shutdown.
                throw new InvalidOperationException("Failed to apply required Drive permissions.", ex);
            }
        }
        return errors;
    }

    /// <summary>
    /// Whether this member's latest grant on this folder from an earlier pass also failed with Drive's internal
    /// error. An unregistered folder logs <see cref="Guid.Empty"/>, so its rows are told apart by the display name
    /// that ends each grant description.
    /// </summary>
    private async Task<bool> LastGrantFailedWithInternalErrorAsync(
        FolderClaim claim, string email, Guid userId, Instant passStart, CancellationToken ct)
    {
        var folderSuffix = $" ({claim.DisplayName})";
        var rows = claim.ResourceId != Guid.Empty
            ? (await googleSyncLogViewer.GetForResourceAsync(claim.ResourceId, ct)).Where(row => row.UserId == userId)
            : (await googleSyncLogViewer.GetForUserAsync(userId, ct)).Where(row =>
                row.ResourceName is null && row.Description.EndsWith(folderSuffix, StringComparison.Ordinal));
        var last = rows.FirstOrDefault(row =>
            row.OccurredAt < passStart
            && row.Action == GoogleSyncLogAction.AccessGranted
            && string.Equals(row.UserEmail, email, StringComparison.OrdinalIgnoreCase));
        return last is { Success: false } && GoogleClientError.IsInternalErrorMessage(last.ErrorMessage);
    }

    /// <summary>Waits between transient-failure attempts; tests replace it to skip the backoff.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private static readonly TimeSpan[] TransientRetryBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// Retries while Google reports a transient error, so a one-off hiccup does not leave a member without access
    /// until the next pass. Permanent failures return at once. <c>Retried</c>: the result came from a repeat.
    /// </summary>
    private async Task<(T Result, bool Retried)> WithTransientRetryAsync<T>(Func<Task<T>> call, Func<T, GoogleClientError?> errorOf, CancellationToken ct)
    {
        var result = await call();
        var retried = false;
        foreach (var backoff in TransientRetryBackoff)
        {
            if (errorOf(result) is not { IsTransient: true } error)
                break;
            logger.LogWarning("Transient Google Drive error (HTTP {StatusCode}), retrying in {Backoff}: {Error}",
                error.StatusCode, backoff, error.RawMessage);
            await Delay(backoff, ct);
            result = await call();
            retried = true;
        }
        return (result, retried);
    }

    /// <summary>Keeps up to two UTF-16 units without splitting a surrogate pair, plus any <c>+tag</c> and domain.</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0) return "***";
        var plus = email.IndexOf('+', 0, at);
        var end = plus > 0 ? plus : at;
        var prefix = StoredGoogleText.Bound(email, Math.Min(2, end));
        return $"{prefix}***{email[end..]}";
    }

    private async Task<List<string>> ApplyExtraAsync(FolderClaim claim, DrivePlan plan, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var member in plan.Members.Where(m => m.State is MemberSyncState.Extra or MemberSyncState.Downgrade))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var commitCt = CancellationToken.None;

                if (!plan.PermissionIdByEmail.TryGetValue(member.Email, out var permissionId))
                    continue;

                if (member.ExpectedRole is not null)
                {
                    // A downgrade, or a mixed permission whose direct elevation is extra: lower
                    // it to the expected role and do not claim all access was removed.
                    if (await UpdateAndLogAsync(claim, member, permissionId, syncSource, commitCt) is { } error) errors.Add(error);
                    continue;
                }

                // Telling someone their access was removed when the delete failed (or the
                // permission turned out to be inherited and untouchable) is a false notice.
                var deletion = await DeleteAndLogAsync(claim, member.Email, member.UserId, permissionId, syncSource, commitCt);
                if (deletion.Error is not null) errors.Add(deletion.Error);
                if (deletion.Deleted) await NotifyRemovalAsync(member.Email, claim, commitCt);
            }
            catch (OperationCanceledException ex)
            {
                // This unit uses None; its cancellation is a dependency fault, not shutdown.
                throw new InvalidOperationException("Failed to remove excess Drive permissions.", ex);
            }
        }
        return errors;
    }

    private async Task<string?> UpdateAndLogAsync(FolderClaim claim, MemberSyncStatus member, string permissionId, GoogleSyncSource syncSource, CancellationToken ct)
    {
        var role = member.ExpectedRole!;
        var (error, _) = await WithTransientRetryAsync(
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


}
