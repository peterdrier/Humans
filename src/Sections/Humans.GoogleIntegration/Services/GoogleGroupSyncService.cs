using Humans.GoogleIntegration.Contracts;
using GroupClaim = Humans.GoogleIntegration.Services.GoogleGroupClaimReader.GroupClaim;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

internal sealed class GoogleGroupSyncService(
    IEnumerable<IGoogleGroupMembershipSource> sources,
    IGoogleGroupMembershipClient membershipClient,
    IGoogleGroupProvisioningClient provisioningClient,
    ITeamResourceGoogleClient teamResourceClient,
    ITeamResourceManagement teamResourceService,
    ITeamService teamService,
    IUserService userService,
    IWorkspaceIdentityManagement workspaceIdentities,
    IUserEmailService userEmailService,
    ISyncSettingsService syncSettingsService,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    IGoogleRemovalNotificationService removalNotifications,
    IGoogleGroupSyncScheduler syncScheduler,
    IOptions<GoogleWorkspaceOptions> options,
    IClock clock,
    ILogger<GoogleGroupSyncService> logger) : IGoogleGroupSync
{
    private static readonly TimeSpan ScopedRetryDelay = TimeSpan.FromMinutes(15);
    private const int MaxScopedRetryAttempts = 5;

    private readonly GoogleWorkspaceOptions _options = options.Value;
    private readonly GoogleGroupClaimReader claimReader = new(sources, teamResourceService, teamService, options.Value, logger);

    public Task RequestSyncAsync(string groupKey, CancellationToken ct = default, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            logger.LogWarning("RequestSyncAsync called with null/empty group key; skipping");
            return Task.CompletedTask;
        }

        syncScheduler.Enqueue(groupKey.Trim(), syncSource);
        return Task.CompletedTask;
    }

    public async Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var claims = await claimReader.LoadClaimsAsync(groupKey: null, ct, syncSource);
        var resourcesByGroup = await claimReader.LoadActiveGroupResourcesByEmailAsync(ct);
        var expectedMembersByUserId = await GoogleSyncMemberHydration.LoadAsync(
            claims.SelectMany(c => c.UserIds).Distinct().ToList(), userService, workspaceIdentities, ct);
        var diffs = new List<ResourceSyncDiff>();

        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            if (claim.IsCollision)
            {
                diffs.Add(await BuildCollisionDiffAsync(claim, resourcesByGroup));
                continue;
            }

            diffs.Add(await ReconcileClaimAsync(
                claim,
                action,
                resourcesByGroup.GetValueOrDefault(claim.GroupKey),
                scheduleRetryOnFailure: action == SyncAction.Execute,
                nextRetryAttempt: 1,
                expectedMembersByUserId,
                ct));
        }

        return new SyncPreviewResult { Diffs = diffs };
    }

    public async Task<ResourceSyncDiff> ReconcileOneAsync(
        string groupKey,
        SyncAction action,
        CancellationToken ct = default,
        int retryAttempt = 0,
        bool scheduleRetries = true,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync)
    {
        var claims = await claimReader.LoadClaimsAsync(groupKey, ct, syncSource);
        var claim = claims.SingleOrDefault(c =>
            string.Equals(c.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase));

        var resourcesByGroup = await claimReader.LoadActiveGroupResourcesByEmailAsync(ct);

        if (claim is null)
        {
            var orphanResource = resourcesByGroup.GetValueOrDefault(groupKey);
            const string error = "No Google group membership source claims this group";
            logger.LogWarning("Google group sync: no source claims group key {GroupKey}", groupKey);
            await RecordGroupErrorAsync(orphanResource, error, ct);
            return BuildErrorDiff(groupKey, orphanResource, error);
        }

        if (claim.IsCollision)
        {
            return await BuildCollisionDiffAsync(claim, resourcesByGroup);
        }

        return await ReconcileClaimAsync(
            claim,
            action,
            resourcesByGroup.GetValueOrDefault(claim.GroupKey),
            scheduleRetryOnFailure: action == SyncAction.Execute && retryAttempt < MaxScopedRetryAttempts && scheduleRetries,
            nextRetryAttempt: retryAttempt + 1,
            expectedMembersByUserId: null,
            ct);
    }

    private async Task<ResourceSyncDiff> ReconcileClaimAsync(
        GroupClaim claim,
        SyncAction action,
        GoogleResourceSnapshot? resource,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        IReadOnlyDictionary<Guid, GoogleSyncMember>? expectedMembersByUserId,
        CancellationToken ct)
    {
        var (lookup, provisionAttempted) = await LookupOrProvisionGroupAsync(claim, action, ct);

        if (lookup.GroupNumericId is null)
        {
            return await BuildClaimErrorDiffAsync(
                claim,
                resource,
                "Google group lookup failed",
                lookup.Error,
                scheduleRetryOnFailure,
                nextRetryAttempt,
                provisionAttempted ? CancellationToken.None : ct);
        }

        expectedMembersByUserId ??= await GoogleSyncMemberHydration.LoadAsync(claim.UserIds, userService, workspaceIdentities, ct);
        var expectedMembers = new Dictionary<string, GoogleSyncMember>(GmailAliasEmailComparer.Instance);
        foreach (var userId in claim.UserIds.Order())
        {
            if (expectedMembersByUserId.TryGetValue(userId, out var member))
            {
                expectedMembers.TryAdd(member.Email, member);
            }
        }

        var list = await membershipClient.ListMembershipsAsync(lookup.GroupNumericId, ct);
        if (list.Memberships is null)
        {
            return await BuildClaimErrorDiffAsync(
                claim,
                resource,
                "Google group membership list failed",
                list.Error,
                scheduleRetryOnFailure,
                nextRetryAttempt,
                ct);
        }

        var plan = await BuildGroupMembershipPlanAsync(expectedMembers, list.Memberships, ct);

        if (action == SyncAction.Execute)
        {
            var errorDiff = await ApplyGroupMembershipChangesAsync(
                claim,
                resource,
                lookup.GroupNumericId,
                plan,
                scheduleRetryOnFailure,
                nextRetryAttempt,
                ct);
            if (errorDiff is not null)
                return errorDiff;
        }

        return new ResourceSyncDiff
        {
            ResourceId = resource?.Id ?? Guid.Empty,
            ResourceName = resource?.Name ?? claim.GroupKey,
            ResourceType = GoogleResourceType.Group.ToString(),
            GoogleId = lookup.GroupNumericId,
            Url = resource?.Url,
            Members = plan.Members
        };
    }

    private async Task<(GroupLookupIdResult Lookup, bool ProvisionAttempted)> LookupOrProvisionGroupAsync(
        GroupClaim claim,
        SyncAction action,
        CancellationToken ct)
    {
        var lookup = await provisioningClient.LookupGroupIdAsync(claim.GroupKey, ct);
        if (lookup.GroupNumericId is not null || !IsGroupNotFound(lookup.Error) || action != SyncAction.Execute)
            return (lookup, false);

        return (await TryProvisionGroupAsync(claim.GroupKey, lookup, ct), true);
    }

    private async Task<GroupLookupIdResult> TryProvisionGroupAsync(
        string groupKey,
        GroupLookupIdResult failedLookup,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Creation, settings and its audit must finish as one provisioning unit.
        var commitCt = CancellationToken.None;
        try
        {
            var create = await provisioningClient.CreateGroupAsync(
                groupKey,
                displayName: groupKey,
                description: $"Auto-provisioned group ({groupKey}).",
                commitCt);

            if (create.GroupNumericId is null)
            {
                logger.LogWarning(
                    "Failed to auto-provision Google Group for {GroupKey}: HTTP {StatusCode} {Message}",
                    groupKey,
                    create.Error?.StatusCode,
                    create.Error?.RawMessage);
                return failedLookup;
            }

            logger.LogInformation("Auto-provisioned Google Group for {GroupKey}", groupKey);
            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.GoogleResourceProvisioned, nameof(GoogleResource), Guid.Empty,
                $"Auto-provisioned Google Group '{groupKey}' ({create.GroupNumericId})",
                nameof(GoogleGroupSyncService));
            await TryApplyProvisionedGroupSettingsAsync(groupKey, commitCt);
            return await provisioningClient.LookupGroupIdAsync(groupKey, commitCt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Error auto-provisioning Google Group for {GroupKey}; treating as failed lookup: {Error}",
                groupKey,
                ex.Message);
            return failedLookup;
        }
    }

    private async Task TryApplyProvisionedGroupSettingsAsync(string groupKey, CancellationToken ct)
    {
        try
        {
            var settingsError = await provisioningClient.UpdateGroupSettingsAsync(
                groupKey,
                GroupSettingsPolicy.BuildExpected(_options.Groups),
                ct);
            if (settingsError is not null)
            {
                logger.LogWarning(
                    "Failed to apply group settings to auto-provisioned {GroupKey} (HTTP {StatusCode}): {Message}. Group was created with Google defaults",
                    groupKey,
                    settingsError.StatusCode,
                    settingsError.RawMessage);
            }
        }
        catch (Exception settingsEx)
        {
            logger.LogWarning(
                "Error applying group settings to auto-provisioned {GroupKey}; continuing with membership reconcile: {Error}",
                groupKey,
                settingsEx.Message);
        }
    }

    private async Task<ResourceSyncDiff> BuildClaimErrorDiffAsync(
        GroupClaim claim,
        GoogleResourceSnapshot? resource,
        string errorPrefix,
        GoogleClientError? googleError,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        CancellationToken ct)
    {
        try
        {
            var error = FormatGoogleError(errorPrefix, googleError);
            await RecordGroupErrorAsync(resource, error, ct);
            logger.LogWarning("Google group sync failed for {GroupKey}: {Error}", claim.GroupKey, error);
            if (scheduleRetryOnFailure)
                await ScheduleRetryAsync(claim.GroupKey, error, nextRetryAttempt, claim.SyncSource);

            return BuildErrorDiff(claim.GroupKey, resource, error);
        }
        catch (OperationCanceledException ex) when (!ct.CanBeCanceled)
        {
            // Failed provisioning finishes its error and retry with None, unlike preflight failures.
            throw new InvalidOperationException("Failed to record the Google Group provisioning failure.", ex);
        }
    }

    private async Task<GroupMembershipPlan> BuildGroupMembershipPlanAsync(
        IReadOnlyDictionary<string, GoogleSyncMember> expectedMembers,
        IReadOnlyList<GroupMembership> memberships,
        CancellationToken ct)
    {
        var currentByEmail = memberships
            .Where(member => !string.IsNullOrWhiteSpace(member.MemberEmail))
            .GroupBy(member => member.MemberEmail!, GmailAliasEmailComparer.Instance)
            .ToDictionary(group => group.Key, group => group.First().ResourceName, GmailAliasEmailComparer.Instance);
        var members = new List<MemberSyncStatus>();
        foreach (var (email, expected) in expectedMembers)
        {
            members.Add(new MemberSyncStatus(
                email,
                expected.DisplayName,
                currentByEmail.ContainsKey(email) ? MemberSyncState.Correct : MemberSyncState.Missing,
                [],
                UserId: expected.UserId,
                ProfilePictureUrl: expected.ProfilePictureUrl));
        }

        var serviceAccountEmail = await teamResourceClient.GetServiceAccountEmailAsync(ct);
        var expectedEmailSet = expectedMembers.Keys.ToHashSet(GmailAliasEmailComparer.Instance);
        var extraEmails = currentByEmail.Keys.Where(email =>
                !expectedEmailSet.Contains(email) &&
                !string.Equals(email, serviceAccountEmail, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // An extra member is a human who left the team, so their id is only reachable
        // from the address Google still holds — without it the revocation row is
        // invisible to /Google/Human/{id} and to their GDPR export.
        var extraUserIdByEmail = await ResolveExtraMemberUserIdsAsync(extraEmails, ct);

        foreach (var email in extraEmails)
        {
            members.Add(new MemberSyncStatus(
                email, email, MemberSyncState.Extra, [],
                UserId: extraUserIdByEmail.TryGetValue(email, out var userId) ? userId : null));
        }

        return new GroupMembershipPlan(members, currentByEmail, expectedMembers);
    }

    private async Task<Dictionary<string, Guid>> ResolveExtraMemberUserIdsAsync(
        IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        if (emails.Count == 0)
            return new Dictionary<string, Guid>(GmailAliasEmailComparer.Instance);

        var owners = UserEmailMatchOwner.ByEmail(await userEmailService.MatchByEmailsAsync(emails, ct));
        return owners.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.UserId, GmailAliasEmailComparer.Instance);
    }

    private async Task<ResourceSyncDiff?> ApplyGroupMembershipChangesAsync(
        GroupClaim claim,
        GoogleResourceSnapshot? resource,
        string groupNumericId,
        GroupMembershipPlan plan,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        CancellationToken ct)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, ct);
        if (mode != SyncMode.None)
        {
            var addError = await ApplyMissingGroupMembersAsync(
                claim, resource, groupNumericId, plan, scheduleRetryOnFailure, nextRetryAttempt, ct);
            if (addError is not null)
                return addError;
        }

        if (mode == SyncMode.AddAndRemove)
        {
            var removeError = await ApplyExtraGroupMembersAsync(
                claim, resource, plan, scheduleRetryOnFailure, nextRetryAttempt, ct);
            if (removeError is not null)
                return removeError;
        }

        if (resource is not null && mode != SyncMode.None)
        {
            try
            {
                await teamResourceService.MarkResourceSyncedAsync(resource.Id, clock.GetCurrentInstant(), CancellationToken.None);
            }
            catch (OperationCanceledException ex)
            {
                throw new InvalidOperationException("Failed to record the Google Group reconciliation outcome.", ex);
            }
        }

        return null;
    }

    private async Task<ResourceSyncDiff?> ApplyMissingGroupMembersAsync(
        GroupClaim claim,
        GoogleResourceSnapshot? resource,
        string groupNumericId,
        GroupMembershipPlan plan,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        CancellationToken ct)
    {
        var members = plan.Members;
        foreach (var member in members.Where(m => m.State == MemberSyncState.Missing))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var commitCt = CancellationToken.None;

                GroupMembershipMutationResult add;
                try
                {
                    add = await membershipClient.CreateMembershipAsync(groupNumericId, member.Email, commitCt);
                }
                catch (OperationCanceledException ex)
                {
                    // The mutation uses None: record a retryable fault, never a target rejection.
                    logger.LogError(ex, "Google Group membership add failed for {Email} in {GroupKey}", member.Email, claim.GroupKey);
                    add = new GroupMembershipMutationResult(GroupMembershipMutationOutcome.Failed, new GoogleClientError(0, ex.Message));
                }
                if (add.Outcome == GroupMembershipMutationOutcome.Failed)
                {
                    var failure = DescribeGroupAddFailure(resource, claim.GroupKey, member.Email, add.Error);
                    // Preserve the vendor failure and its retry before the separate Users write.
                    var diff = await BuildMemberMutationErrorDiffAsync(
                        claim,
                        resource,
                        member.Email,
                        failure.Error,
                        GoogleSyncLogAction.AccessGranted,
                        members,
                        scheduleRetryOnFailure,
                        nextRetryAttempt,
                        member.UserId,
                        commitCt);
                    if (failure.RejectTarget)
                    {
                        var target = plan.ExpectedByEmail[member.Email];
                        await workspaceIdentities.SetStatusFromSyncAsync(
                            target.UserId, target.IdentityEmail, GoogleEmailStatus.Rejected, commitCt);
                        logger.LogWarning(
                            "Google rejected target member email {Email} while adding to Google Group {GroupKey} ({GroupId}) - HTTP {StatusCode}. " +
                            "Google email status marked Rejected for the address when a matching user was found. Google error: {ErrorMessage}",
                            member.Email, claim.GroupKey, resource?.GoogleId, add.Error?.StatusCode, add.Error?.RawMessage);
                    }
                    return diff;
                }

                if (add.Outcome == GroupMembershipMutationOutcome.Added)
                {
                    await googleSyncLog.LogAsync(
                        GoogleSyncLogAction.AccessGranted,
                        resource?.Id ?? Guid.Empty,
                        $"Granted Google Group access to {member.Email} ({resource?.Name ?? claim.GroupKey})",
                        nameof(GoogleGroupSyncService),
                        member.Email,
                        "MEMBER",
                        claim.SyncSource,
                        success: true,
                        userId: member.UserId,
                        ct: commitCt);
                }
            }
            catch (OperationCanceledException ex)
            {
                throw new InvalidOperationException("Failed to finish Google Group membership tracking.", ex);
            }
        }

        return null;
    }

    private async Task<ResourceSyncDiff?> ApplyExtraGroupMembersAsync(
        GroupClaim claim,
        GoogleResourceSnapshot? resource,
        GroupMembershipPlan plan,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        CancellationToken ct)
    {
        foreach (var member in plan.Members.Where(m => m.State == MemberSyncState.Extra))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var commitCt = CancellationToken.None;

                if (!plan.CurrentByEmail.TryGetValue(member.Email, out var membershipResourceName))
                    continue;

                GoogleClientError? deleteError;
                try
                {
                    deleteError = await membershipClient.DeleteMembershipAsync(membershipResourceName, commitCt);
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogError(ex, "Google Group membership removal failed for {Email} in {GroupKey}", member.Email, claim.GroupKey);
                    deleteError = new GoogleClientError(0, ex.Message);
                }
                if (deleteError is not null)
                {
                    return await BuildMemberMutationErrorDiffAsync(
                        claim,
                        resource,
                        member.Email,
                        FormatGoogleError($"Google group remove failed for {member.Email}", deleteError),
                        GoogleSyncLogAction.AccessRevoked,
                        plan.Members,
                        scheduleRetryOnFailure,
                        nextRetryAttempt,
                        member.UserId,
                        commitCt);
                }

                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessRevoked,
                    resource?.Id ?? Guid.Empty,
                    $"Removed {member.Email} from Google Group ({resource?.Name ?? claim.GroupKey})",
                    nameof(GoogleGroupSyncService),
                    member.Email,
                    "MEMBER",
                    claim.SyncSource,
                    success: true,
                    userId: member.UserId,
                    ct: commitCt);

                await NotifyRemovalAsync(member.Email, resource, claim.GroupKey, commitCt);
            }
            catch (OperationCanceledException ex)
            {
                throw new InvalidOperationException("Failed to finish Google Group membership tracking.", ex);
            }
        }

        return null;
    }

    private async Task<ResourceSyncDiff> BuildMemberMutationErrorDiffAsync(
        GroupClaim claim,
        GoogleResourceSnapshot? resource,
        string email,
        string error,
        GoogleSyncLogAction syncAction,
        List<MemberSyncStatus> members,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        Guid? userId,
        CancellationToken ct)
    {
        logger.LogWarning(
            "Google group sync failed for {GroupKey} member {Email}: {Error}",
            claim.GroupKey,
            email,
            error);
        if (resource is not null)
            await teamResourceService.RecordResourceErrorAsync(resource.Id, error, ct);

        await googleSyncLog.LogAsync(
            syncAction,
            resource?.Id ?? Guid.Empty,
            $"{error} (group {claim.GroupKey})",
            nameof(GoogleGroupSyncService),
            email,
            "MEMBER",
            claim.SyncSource,
            success: false,
            errorMessage: error,
            userId: userId,
            ct: ct);

        if (scheduleRetryOnFailure)
            await ScheduleRetryAsync(claim.GroupKey, error, nextRetryAttempt, claim.SyncSource);

        return BuildErrorDiff(claim.GroupKey, resource, error, members);
    }

    private async Task<ResourceSyncDiff> BuildCollisionDiffAsync(
        GroupClaim claim,
        IReadOnlyDictionary<string, GoogleResourceSnapshot> resourcesByGroup)
    {
        var error = $"Google group membership source collision for {claim.GroupKey}: {string.Join(", ", claim.SourceNames)}";
        logger.LogError("{Error}", error);

        await auditLogService.LogAsync(
            GoogleIntegrationAuditActions.AnomalousPermissionDetected,
            nameof(GoogleResource),
            Guid.Empty,
            error,
            nameof(GoogleGroupSyncService));

        return BuildErrorDiff(claim.GroupKey, resourcesByGroup.GetValueOrDefault(claim.GroupKey), error);
    }

    private Task RecordGroupErrorAsync(GoogleResourceSnapshot? resource, string error, CancellationToken ct)
    {
        if (resource is null)
            return Task.CompletedTask;

        return teamResourceService.RecordResourceErrorAsync(resource.Id, error, ct);
    }

    private (string Error, bool RejectTarget) DescribeGroupAddFailure(
        GoogleResourceSnapshot? resource,
        string groupKey,
        string email,
        GoogleClientError? error)
    {
        var statusCode = error?.StatusCode ?? 0;
        var rawMessage = error?.RawMessage ?? string.Empty;

        // Cloud Identity answers 403, or 400 "precondition check failed", when the target has no Google account.
        // Both are a permanent target rejection.
        if (statusCode != 403 && statusCode != 400)
        {
            return (FormatGoogleError($"Google group add failed for {email}", error), false);
        }

        if (statusCode == 403)
        {
            var isCallerPermissionError = rawMessage.Contains("caller", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("service account", StringComparison.OrdinalIgnoreCase)
                || rawMessage.Contains("does not have permission", StringComparison.OrdinalIgnoreCase);

            if (isCallerPermissionError)
            {
                logger.LogWarning(
                    "Service account lacks permission to add members to group {GroupKey} ({GroupId}) - HTTP 403: {ErrorMessage}",
                    groupKey,
                    resource?.GoogleId,
                    rawMessage);
                return (FormatGoogleError($"Google group add failed for {email}", error), false);
            }
        }

        if (IsTargetMemberRejection(rawMessage))
        {
            return ($"Google rejected {email} for group membership - no Google account for this address (HTTP {statusCode})", true);
        }

        return (FormatGoogleError($"Google group add failed for {email}", error), false);
    }

    /// <summary>Group path only: Drive has its own predicate.</summary>
    internal static bool IsTargetMemberRejection(string rawMessage)
        => rawMessage.Contains("invalid member", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("invalid email", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("invalid user", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("does not have a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("no google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not associated with a google account", StringComparison.OrdinalIgnoreCase)
            // "precondition check failed" is safe HERE (group path) but NOT on Drive (sharing-policy overlap).
            || rawMessage.Contains("precondition check failed", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("error(4002)", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("membership cannot be created", StringComparison.OrdinalIgnoreCase);

    private async Task ScheduleRetryAsync(string groupKey, string error, int retryAttempt, GoogleSyncSource syncSource)
    {
        logger.LogWarning(
            "Scheduling scoped Google Group membership retry {RetryAttempt}/{MaxRetryAttempts} for {GroupKey} in {DelayMinutes} minutes after failure: {Error}",
            retryAttempt,
            MaxScopedRetryAttempts,
            groupKey,
            ScopedRetryDelay.TotalMinutes,
            error);

        syncScheduler.Schedule(groupKey, ScopedRetryDelay, retryAttempt, syncSource);

        await auditLogService.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            nameof(GoogleResource),
            Guid.Empty,
            $"Scheduled retry {retryAttempt}/{MaxScopedRetryAttempts} for {groupKey}: {error}",
            nameof(GoogleGroupSyncService));
    }

    private async Task NotifyRemovalAsync(
        string email,
        GoogleResourceSnapshot? resource,
        string groupKey,
        CancellationToken ct)
    {
        try
        {
            await removalNotifications.NotifyRemovalAsync(
                email,
                GoogleResourceType.Group,
                resource?.Name ?? groupKey,
                groupKey,
                SyncRemovalReason.Reconciliation,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to enqueue Google Group removal notification for {Email} from {GroupKey}",
                email,
                groupKey);
        }
    }

    private static ResourceSyncDiff BuildErrorDiff(
        string groupKey,
        GoogleResourceSnapshot? resource,
        string error,
        List<MemberSyncStatus>? members = null) => new()
        {
            ResourceId = resource?.Id ?? Guid.Empty,
            ResourceName = resource?.Name ?? groupKey,
            ResourceType = GoogleResourceType.Group.ToString(),
            GoogleId = resource?.GoogleId,
            Url = resource?.Url,
            ErrorMessage = error,
            Members = members ?? []
        };

    private static string FormatGoogleError(string prefix, GoogleClientError? error) =>
        $"{prefix} (HTTP {error?.StatusCode ?? 0}): {error?.RawMessage}";

    /// <summary>
    /// A missed Group lookup returns 404, or sometimes 403 because Google won't confirm a group the caller can't see.
    /// Both trigger auto-provision; a real permission problem fails CreateGroup the same way.
    /// </summary>
    private static bool IsGroupNotFound(GoogleClientError? error)
        => error is { StatusCode: 404 } || error is { StatusCode: 403 };

    private sealed record GroupMembershipPlan(
        List<MemberSyncStatus> Members,
        Dictionary<string, string> CurrentByEmail,
        IReadOnlyDictionary<string, GoogleSyncMember> ExpectedByEmail);
}
