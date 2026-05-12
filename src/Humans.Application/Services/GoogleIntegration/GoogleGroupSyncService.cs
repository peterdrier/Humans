using Humans.Application.DTOs;
using Humans.Application.Configuration;
using Humans.Application.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.GoogleIntegration;

public sealed class GoogleGroupSyncService : IGoogleGroupSync
{
    private static readonly TimeSpan ScopedRetryDelay = TimeSpan.FromMinutes(15);
    private const int MaxScopedRetryAttempts = 5;

    private readonly IEnumerable<IGoogleGroupMembershipSource> _sources;
    private readonly IGoogleGroupMembershipClient _membershipClient;
    private readonly IGoogleGroupProvisioningClient _provisioningClient;
    private readonly ITeamResourceGoogleClient _teamResourceClient;
    private readonly ITeamResourceService _teamResourceService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IProfileService _profileService;
    private readonly ISyncSettingsService _syncSettingsService;
    private readonly IAuditLogService _auditLogService;
    private readonly IGoogleRemovalNotificationService _removalNotifications;
    private readonly IGoogleGroupSyncScheduler _syncScheduler;
    private readonly GoogleWorkspaceOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<GoogleGroupSyncService> _logger;

    public GoogleGroupSyncService(
        IEnumerable<IGoogleGroupMembershipSource> sources,
        IGoogleGroupMembershipClient membershipClient,
        IGoogleGroupProvisioningClient provisioningClient,
        ITeamResourceGoogleClient teamResourceClient,
        ITeamResourceService teamResourceService,
        ITeamService teamService,
        IUserService userService,
        IUserEmailService userEmailService,
        IProfileService profileService,
        ISyncSettingsService syncSettingsService,
        IAuditLogService auditLogService,
        IGoogleRemovalNotificationService removalNotifications,
        IGoogleGroupSyncScheduler syncScheduler,
        IOptions<GoogleWorkspaceOptions> options,
        IClock clock,
        ILogger<GoogleGroupSyncService> logger)
    {
        _sources = sources;
        _membershipClient = membershipClient;
        _provisioningClient = provisioningClient;
        _teamResourceClient = teamResourceClient;
        _teamResourceService = teamResourceService;
        _teamService = teamService;
        _userService = userService;
        _userEmailService = userEmailService;
        _profileService = profileService;
        _syncSettingsService = syncSettingsService;
        _auditLogService = auditLogService;
        _removalNotifications = removalNotifications;
        _syncScheduler = syncScheduler;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public Task RequestSyncAsync(string groupKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            _logger.LogWarning("RequestSyncAsync called with null/empty group key; skipping");
            return Task.CompletedTask;
        }

        _syncScheduler.Enqueue(groupKey.Trim());
        return Task.CompletedTask;
    }

    public async Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default)
    {
        var claims = await LoadClaimsAsync(groupKey: null, ct);
        var resourcesByGroup = await LoadActiveGroupResourcesByEmailAsync(ct);
        var expectedMembersByUserId = await HydrateExpectedMembersByUserIdAsync(
            claims.SelectMany(c => c.UserIds).Distinct().ToList(),
            ct);
        var diffs = new List<ResourceSyncDiff>();

        foreach (var claim in claims)
        {
            if (claim.IsCollision)
            {
                diffs.Add(await BuildCollisionDiffAsync(claim, resourcesByGroup, ct));
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
        int retryAttempt = 0)
    {
        var claims = await LoadClaimsAsync(groupKey, ct);
        var claim = claims.SingleOrDefault(c =>
            string.Equals(c.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase));

        var resourcesByGroup = await LoadActiveGroupResourcesByEmailAsync(ct);

        if (claim is null)
        {
            var orphanResource = resourcesByGroup.GetValueOrDefault(groupKey);
            const string error = "No Google group membership source claims this group";
            _logger.LogWarning("Google group sync: no source claims group key {GroupKey}", groupKey);
            await RecordGroupErrorAsync(orphanResource, error, ct);
            return BuildErrorDiff(groupKey, orphanResource, error);
        }

        if (claim.IsCollision)
        {
            return await BuildCollisionDiffAsync(claim, resourcesByGroup, ct);
        }

        return await ReconcileClaimAsync(
            claim,
            action,
            resourcesByGroup.GetValueOrDefault(claim.GroupKey),
            scheduleRetryOnFailure: action == SyncAction.Execute && retryAttempt < MaxScopedRetryAttempts,
            nextRetryAttempt: retryAttempt + 1,
            expectedMembersByUserId: null,
            ct);
    }

    private async Task<IReadOnlyList<GroupClaim>> LoadClaimsAsync(
        string? groupKey,
        CancellationToken ct)
    {
        var claims = new List<(string SourceName, string GroupKey, Guid[] UserIds)>();

        foreach (var source in _sources)
        {
            var expected = await source.GetExpectedAsync(groupKey, ct);
            foreach (var (key, userIds) in expected)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                claims.Add((source.GetType().Name, key.Trim(), userIds.Distinct().ToArray()));
            }
        }

        return claims
            .GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupClaim(
                g.Key,
                g.Count(),
                g.Select(c => c.SourceName).Distinct(StringComparer.Ordinal).ToArray(),
                g.SelectMany(c => c.UserIds).Distinct().ToArray()))
            .ToList();
    }

    private async Task<ResourceSyncDiff> ReconcileClaimAsync(
        GroupClaim claim,
        SyncAction action,
        GoogleResource? resource,
        bool scheduleRetryOnFailure,
        int nextRetryAttempt,
        IReadOnlyDictionary<Guid, ExpectedMember>? expectedMembersByUserId,
        CancellationToken ct)
    {
        var lookup = await _provisioningClient.LookupGroupIdAsync(claim.GroupKey, ct);
        if (lookup.GroupNumericId is null)
        {
            var error = FormatGoogleError("Google group lookup failed", lookup.Error);
            await RecordGroupErrorAsync(resource, error, ct);
            _logger.LogWarning(
                "Google group sync failed for {GroupKey}: {Error}",
                claim.GroupKey,
                error);
            if (scheduleRetryOnFailure)
                await ScheduleRetryAsync(claim.GroupKey, error, nextRetryAttempt);
            return BuildErrorDiff(claim.GroupKey, resource, error);
        }

        expectedMembersByUserId ??= await HydrateExpectedMembersByUserIdAsync(claim.UserIds, ct);
        var expectedMembers = BuildExpectedMembersByEmail(claim.UserIds, expectedMembersByUserId);
        var expectedEmails = expectedMembers.Keys.ToHashSet(NormalizingEmailComparer.Instance);

        var list = await _membershipClient.ListMembershipsAsync(lookup.GroupNumericId, ct);
        if (list.Memberships is null)
        {
            var error = FormatGoogleError("Google group membership list failed", list.Error);
            await RecordGroupErrorAsync(resource, error, ct);
            _logger.LogWarning(
                "Google group sync failed for {GroupKey}: {Error}",
                claim.GroupKey,
                error);
            if (scheduleRetryOnFailure)
                await ScheduleRetryAsync(claim.GroupKey, error, nextRetryAttempt);
            return BuildErrorDiff(claim.GroupKey, resource, error);
        }

        var currentByEmail = list.Memberships
            .Where(m => !string.IsNullOrWhiteSpace(m.MemberEmail))
            .GroupBy(m => m.MemberEmail!, NormalizingEmailComparer.Instance)
            .ToDictionary(g => g.Key, g => g.First().ResourceName, NormalizingEmailComparer.Instance);

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

        var serviceAccountEmail = await _teamResourceClient.GetServiceAccountEmailAsync(ct);
        foreach (var email in currentByEmail.Keys
                     .Where(email =>
                         !expectedEmails.Contains(email) &&
                         !string.Equals(email, serviceAccountEmail, StringComparison.OrdinalIgnoreCase)))
        {
            members.Add(new MemberSyncStatus(email, email, MemberSyncState.Extra, []));
        }

        if (action == SyncAction.Execute)
        {
            var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, ct);
            if (mode != SyncMode.None)
            {
                foreach (var member in members.Where(m => m.State == MemberSyncState.Missing))
                {
                    var add = await _membershipClient.CreateMembershipAsync(lookup.GroupNumericId, member.Email, ct);
                    if (add.Outcome == GroupMembershipMutationOutcome.Failed)
                    {
                        var error = await HandleGroupAddFailureAsync(resource, claim.GroupKey, member.Email, add.Error, ct);
                        _logger.LogWarning(
                            "Google group sync failed for {GroupKey} member {Email}: {Error}",
                            claim.GroupKey,
                            member.Email,
                            error);
                        await RecordMemberErrorAsync(
                            resource,
                            member.Email,
                            error,
                            AuditAction.GoogleResourceAccessGranted,
                            ct);
                        if (scheduleRetryOnFailure)
                            await ScheduleRetryAsync(claim.GroupKey, error, nextRetryAttempt);
                        return BuildErrorDiff(claim.GroupKey, resource, error, members);
                    }

                    if (resource is not null && add.Outcome == GroupMembershipMutationOutcome.Added)
                    {
                        await _auditLogService.LogGoogleSyncAsync(
                            AuditAction.GoogleResourceAccessGranted,
                            resource.Id,
                            $"Granted Google Group access to {member.Email} ({resource.Name})",
                            nameof(GoogleGroupSyncService),
                            member.Email,
                            "MEMBER",
                            GoogleSyncSource.ScheduledSync,
                            success: true);
                    }
                }
            }

            if (mode == SyncMode.AddAndRemove)
            {
                foreach (var member in members.Where(m => m.State == MemberSyncState.Extra))
                {
                    if (!currentByEmail.TryGetValue(member.Email, out var membershipResourceName))
                        continue;

                    var deleteError = await _membershipClient.DeleteMembershipAsync(membershipResourceName, ct);
                    if (deleteError is not null)
                    {
                        var error = FormatGoogleError($"Google group remove failed for {member.Email}", deleteError);
                        _logger.LogWarning(
                            "Google group sync failed for {GroupKey} member {Email}: {Error}",
                            claim.GroupKey,
                            member.Email,
                            error);
                        await RecordMemberErrorAsync(
                            resource,
                            member.Email,
                            error,
                            AuditAction.GoogleResourceAccessRevoked,
                            ct);
                        if (scheduleRetryOnFailure)
                            await ScheduleRetryAsync(claim.GroupKey, error, nextRetryAttempt);
                        return BuildErrorDiff(claim.GroupKey, resource, error, members);
                    }

                    if (resource is not null)
                    {
                        await _auditLogService.LogGoogleSyncAsync(
                            AuditAction.GoogleResourceAccessRevoked,
                            resource.Id,
                            $"Removed {member.Email} from Google Group ({resource.Name})",
                            nameof(GoogleGroupSyncService),
                            member.Email,
                            "MEMBER",
                            GoogleSyncSource.ScheduledSync,
                            success: true);
                    }

                    await NotifyRemovalAsync(member.Email, resource, claim.GroupKey, ct);
                }
            }

            if (resource is not null && mode != SyncMode.None)
                await _teamResourceService.MarkResourceSyncedAsync(resource.Id, _clock.GetCurrentInstant(), ct);
        }

        return new ResourceSyncDiff
        {
            ResourceId = resource?.Id ?? Guid.Empty,
            ResourceName = resource?.Name ?? claim.GroupKey,
            ResourceType = GoogleResourceType.Group.ToString(),
            GoogleId = lookup.GroupNumericId,
            Url = resource?.Url,
            Members = members
        };
    }

    private async Task<Dictionary<Guid, ExpectedMember>> HydrateExpectedMembersByUserIdAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
            return [];

        var users = await _userService.GetByIdsAsync(userIds, ct);
        var emailsByUserId = await _userEmailService.GetEntitiesByUserIdsAsync(userIds, ct);
        var profilesByUserId = await _profileService.GetByUserIdsAsync(userIds, ct);

        var result = new Dictionary<Guid, ExpectedMember>();
        foreach (var userId in userIds)
        {
            if (!users.TryGetValue(userId, out var user))
                continue;
            if (user.GoogleEmailStatus == GoogleEmailStatus.Rejected || user.IsDeletionPending || user.MergedToUserId is not null)
                continue;
            profilesByUserId.TryGetValue(userId, out var profile);
            if (profile is not null && IsSuspended(profile))
                continue;

            var email = TryGetGoogleEmail(userId, emailsByUserId);
            if (email is null)
                continue;

            var displayName = profile is not null && !string.IsNullOrWhiteSpace(profile.BurnerName)
                ? profile.BurnerName
                : user.DisplayName;

            result[user.Id] = new ExpectedMember(user.Id, email, displayName, user.ProfilePictureUrl);
        }

        return result;
    }

    private static Dictionary<string, ExpectedMember> BuildExpectedMembersByEmail(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyDictionary<Guid, ExpectedMember> expectedMembersByUserId)
    {
        var result = new Dictionary<string, ExpectedMember>(NormalizingEmailComparer.Instance);
        foreach (var userId in userIds)
        {
            if (expectedMembersByUserId.TryGetValue(userId, out var member))
                result[member.Email] = member;
        }

        return result;
    }

    private static string? TryGetGoogleEmail(
        Guid userId,
        IReadOnlyDictionary<Guid, IReadOnlyList<UserEmail>> emailsByUserId)
    {
        var emails = emailsByUserId.TryGetValue(userId, out var list)
            ? list
            : Array.Empty<UserEmail>();

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

    private static bool IsSuspended(Humans.Domain.Entities.Profile profile)
    {
        if (profile.State == ProfileState.Suspended)
            return true;

#pragma warning disable HUM_PROFILE_ISSUSPENDED
        return profile.IsSuspended;
#pragma warning restore HUM_PROFILE_ISSUSPENDED
    }

    private async Task<ResourceSyncDiff> BuildCollisionDiffAsync(
        GroupClaim claim,
        IReadOnlyDictionary<string, GoogleResource> resourcesByGroup,
        CancellationToken ct)
    {
        var error = $"Google group membership source collision for {claim.GroupKey}: {string.Join(", ", claim.SourceNames)}";
        _logger.LogError("{Error}", error);

        await _auditLogService.LogAsync(
            AuditAction.AnomalousPermissionDetected,
            nameof(GoogleResource),
            Guid.Empty,
            error,
            nameof(GoogleGroupSyncService));

        return BuildErrorDiff(claim.GroupKey, resourcesByGroup.GetValueOrDefault(claim.GroupKey), error);
    }

    private async Task<IReadOnlyDictionary<string, GoogleResource>> LoadActiveGroupResourcesByEmailAsync(CancellationToken ct)
    {
        var counts = await _teamResourceService.GetActiveResourceCountsByTeamAsync(ct);
        if (counts.Count == 0)
            return new Dictionary<string, GoogleResource>(StringComparer.OrdinalIgnoreCase);

        var byTeam = await _teamResourceService.GetResourcesByTeamIdsAsync(counts.Keys.ToList(), ct);
        var teamIds = byTeam.Keys.ToList();
        var teamsById = new Dictionary<Guid, Team?>();
        foreach (var teamId in teamIds)
            teamsById[teamId] = await _teamService.GetTeamByIdAsync(teamId, ct);

        var resourcesByEmail = byTeam
            .SelectMany(kvp => kvp.Value.Select(resource => (TeamId: kvp.Key, Resource: resource)))
            .Where(x => x.Resource.ResourceType == GoogleResourceType.Group && x.Resource.IsActive)
            .Select(x =>
            {
                teamsById.TryGetValue(x.TeamId, out var team);
                return (x.Resource, Email: GoogleGroupKeyHelper.TryGetGroupKey(
                    x.Resource,
                    team?.GoogleGroupEmail,
                    _options.Domain));
            })
            .Where(x => x.Email is not null)
            .GroupBy(x => x.Email!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in resourcesByEmail.Where(g => g.Count() > 1))
        {
            var kept = group.First().Resource;
            _logger.LogWarning(
                "Multiple active Google group resource rows resolve to {GroupKey}; using {ResourceId} and ignoring {DuplicateCount} duplicate rows",
                group.Key,
                kept.Id,
                group.Count() - 1);
        }

        return resourcesByEmail.ToDictionary(g => g.Key, g => g.First().Resource, StringComparer.OrdinalIgnoreCase);
    }

    private Task RecordGroupErrorAsync(GoogleResource? resource, string error, CancellationToken ct)
    {
        if (resource is null)
            return Task.CompletedTask;

        return _teamResourceService.RecordResourceErrorAsync(resource.Id, error, ct);
    }

    private async Task RecordMemberErrorAsync(
        GoogleResource? resource,
        string email,
        string error,
        AuditAction action,
        CancellationToken ct)
    {
        if (resource is not null)
        {
            await _teamResourceService.RecordResourceErrorAsync(resource.Id, error, ct);
            await _auditLogService.LogGoogleSyncAsync(
                action,
                resource.Id,
                error,
                nameof(GoogleGroupSyncService),
                email,
                "MEMBER",
                GoogleSyncSource.ScheduledSync,
                success: false,
                errorMessage: error);
        }
    }

    private async Task<string> HandleGroupAddFailureAsync(
        GoogleResource? resource,
        string groupKey,
        string email,
        GoogleClientError? error,
        CancellationToken ct)
    {
        var statusCode = error?.StatusCode ?? 0;
        var rawMessage = error?.RawMessage ?? string.Empty;

        if (statusCode != 403)
        {
            return FormatGoogleError($"Google group add failed for {email}", error);
        }

        var isCallerPermissionError = rawMessage.Contains("caller", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("service account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("does not have permission", StringComparison.OrdinalIgnoreCase);

        if (isCallerPermissionError)
        {
            _logger.LogWarning(
                "Service account lacks permission to add members to group {GroupKey} ({GroupId}) - HTTP 403: {ErrorMessage}",
                groupKey,
                resource?.GoogleId,
                rawMessage);
            return FormatGoogleError($"Google group add failed for {email}", error);
        }

        if (IsTargetMemberRejection(rawMessage))
        {
            var user = await _userService.GetByEmailOrAlternateAsync(email, ct);
            if (user is not null && user.GoogleEmailStatus != GoogleEmailStatus.Rejected)
            {
                await _userService.TrySetGoogleEmailStatusFromSyncAsync(
                    user.Id,
                    GoogleEmailStatus.Rejected,
                    ct);
            }

            _logger.LogWarning(
                "Google rejected target member email {Email} while adding to Google Group {GroupKey} ({GroupId}) - HTTP 403. " +
                "Legacy User.GoogleEmailStatus was marked Rejected when a matching user was found. Google error: {ErrorMessage}",
                email,
                groupKey,
                resource?.GoogleId,
                rawMessage);

            return $"Google rejected {email} for group membership - no Google account for this address (HTTP 403)";
        }

        return FormatGoogleError($"Google group add failed for {email}", error);
    }

    private static bool IsTargetMemberRejection(string rawMessage)
        => rawMessage.Contains("does not have a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("not associated with a google account", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("invalid member", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("invalid email", StringComparison.OrdinalIgnoreCase)
            || rawMessage.Contains("invalid user", StringComparison.OrdinalIgnoreCase);

    private async Task ScheduleRetryAsync(string groupKey, string error, int retryAttempt)
    {
        _logger.LogWarning(
            "Scheduling scoped Google Group membership retry {RetryAttempt}/{MaxRetryAttempts} for {GroupKey} in {DelayMinutes} minutes after failure: {Error}",
            retryAttempt,
            MaxScopedRetryAttempts,
            groupKey,
            ScopedRetryDelay.TotalMinutes,
            error);

        _syncScheduler.Schedule(groupKey, ScopedRetryDelay, retryAttempt);

        await _auditLogService.LogAsync(
            AuditAction.GoogleSyncRetryScheduled,
            nameof(GoogleResource),
            Guid.Empty,
            $"Scheduled retry {retryAttempt}/{MaxScopedRetryAttempts} for {groupKey}: {error}",
            nameof(GoogleGroupSyncService));
    }

    private async Task NotifyRemovalAsync(
        string email,
        GoogleResource? resource,
        string groupKey,
        CancellationToken ct)
    {
        try
        {
            await _removalNotifications.NotifyRemovalAsync(
                email,
                GoogleResourceType.Group,
                resource?.Name ?? groupKey,
                groupKey,
                SyncRemovalReason.Reconciliation,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to enqueue Google Group removal notification for {Email} from {GroupKey}",
                email,
                groupKey);
        }
    }

    private static ResourceSyncDiff BuildErrorDiff(
        string groupKey,
        GoogleResource? resource,
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

    private sealed record GroupClaim(string GroupKey, int ClaimCount, string[] SourceNames, Guid[] UserIds)
    {
        public bool IsCollision => ClaimCount > 1;
    }

    private sealed record ExpectedMember(Guid UserId, string Email, string DisplayName, string? ProfilePictureUrl);
}
