using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Governance;

namespace Humans.Application.Services.Governance;

public sealed class MembershipCalculator(
    IMembershipQuery membershipQuery,
    IUserServiceRead userService,
    ILegalDocumentSyncService legalDocumentSyncService,
    IServiceProvider serviceProvider,
    IClock clock) : IMembershipCalculator
{
    // IMembershipQuery (not ITeamService/IRoleAssignmentService) breaks DI cycle through ISystemTeamSync.

    // Lazy IConsentServiceRead resolve — ConsentService depends on IMembershipCalculator.

    private IConsentServiceRead ConsentService => serviceProvider.GetRequiredService<IConsentServiceRead>();

    public async Task<MembershipStatus> ComputeStatusAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);

        if (info?.Profile is null)
        {
            return MembershipStatus.None;
        }

        if (info.IsSuspended)
        {
            return MembershipStatus.Suspended;
        }

        if (!info.Profile.IsApproved)
        {
            return MembershipStatus.Pending;
        }

        // Active = has role assignments OR is a Volunteers-team member.
        var hasActiveRoles = await HasActiveRolesAsync(userId, cancellationToken);
        var isVolunteerMember = await membershipQuery.IsUserMemberOfTeamAsync(
            SystemTeamIds.Volunteers, userId, cancellationToken);

        if (!hasActiveRoles && !isVolunteerMember)
        {
            return MembershipStatus.None;
        }

        var hasExpiredConsents = await HasAnyExpiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);
        if (hasExpiredConsents)
        {
            return MembershipStatus.Inactive;
        }

        return MembershipStatus.Active;
    }

    public async Task<MembershipSnapshot> GetMembershipSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var status = await ComputeStatusAsync(userId, cancellationToken);

        var eligibleTeamIds = await GetRequiredTeamIdsForUserAsync(userId, cancellationToken);
        var allRequiredVersionIds = new List<Guid>();

        foreach (var teamId in eligibleTeamIds)
        {
            var versions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, cancellationToken);
            allRequiredVersionIds.AddRange(versions.Select(v => v.Id));
        }

        var requiredVersionIds = allRequiredVersionIds.Distinct().ToList();

        var consentedVersionIds = await ConsentService.GetConsentedVersionIdsAsync(userId, cancellationToken);
        var missingVersionIds = requiredVersionIds
            .Where(id => !consentedVersionIds.Contains(id))
            .ToList();

        var isVolunteerMember = await membershipQuery.IsUserMemberOfTeamAsync(
            SystemTeamIds.Volunteers, userId, cancellationToken);

        return new MembershipSnapshot(
            status,
            isVolunteerMember,
            requiredVersionIds.Count,
            missingVersionIds.Count,
            missingVersionIds);
    }

    public Task<bool> HasAllRequiredConsentsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);

    public async Task<bool> HasAllRequiredConsentsForTeamAsync(
        Guid userId,
        Guid teamId,
        CancellationToken ct = default)
    {
        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
        {
            return true;
        }

        var consentedVersionIds = await ConsentService.GetConsentedVersionIdsAsync(userId, ct);
        return requiredVersions.All(v => consentedVersionIds.Contains(v.Id));
    }

    public Task<bool> HasAnyExpiredConsentsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        HasAnyExpiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);

    public async Task<bool> HasAnyExpiredConsentsForTeamAsync(
        Guid userId,
        Guid teamId,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        var consentedVersionIds = await ConsentService.GetConsentedVersionIdsAsync(userId, ct);

        return requiredVersions.Any(v =>
            !consentedVersionIds.Contains(v.Id) &&
            v.EffectiveFrom + Duration.FromDays(v.LegalDocumentGracePeriodDays) <= now);
    }

    public async Task<IReadOnlyList<Guid>> GetMissingConsentVersionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, cancellationToken);
        var consentedVersionIds = await ConsentService.GetConsentedVersionIdsAsync(userId, cancellationToken);

        return requiredVersions
            .Select(v => v.Id)
            .Where(id => !consentedVersionIds.Contains(id))
            .ToList();
    }

    public Task<bool> HasActiveRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        membershipQuery.HasAnyActiveAssignmentAsync(userId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetUsersRequiringStatusUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        var usersWithActiveRoles = await membershipQuery.GetUserIdsWithActiveAssignmentsAsync(cancellationToken);

        var usersWithAnyExpiredConsents = await GetUsersWithAnyExpiredConsentsAsync(usersWithActiveRoles, cancellationToken);

        return usersWithAnyExpiredConsents.ToList();
    }

    public Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default) =>
        GetUsersWithAllRequiredConsentsForTeamAsync(userIds, SystemTeamIds.Volunteers, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds,
        Guid teamId,
        CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(teamId, ct);
        if (requiredVersions.Count == 0)
        {
            return userIdList.ToHashSet();
        }

        var consentsByUser = await ConsentService.GetConsentMapForUsersAsync(userIdList, ct);

        var requiredSet = requiredVersions.Select(v => v.Id).ToHashSet();
        var result = new HashSet<Guid>();

        foreach (var userId in userIdList)
        {
            if (consentsByUser.TryGetValue(userId, out var consented) &&
                requiredSet.All(consented.Contains))
            {
                result.Add(userId);
            }
        }

        return result;
    }

    public async Task<IReadOnlySet<Guid>> GetUsersWithAnyExpiredConsentsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var now = clock.GetCurrentInstant();

        var requiredVersions = await legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(SystemTeamIds.Volunteers, cancellationToken);
        var expiredVersionIds = requiredVersions
            .Where(v =>
                v.EffectiveFrom + Duration.FromDays(v.LegalDocumentGracePeriodDays) <= now)
            .Select(v => v.Id)
            .ToHashSet();

        if (expiredVersionIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var consentsByUser = await ConsentService.GetConsentMapForUsersAsync(userIdList, cancellationToken);

        var result = new HashSet<Guid>();
        foreach (var userId in userIdList)
        {
            if (!consentsByUser.TryGetValue(userId, out var consented) ||
                expiredVersionIds.Any(id => !consented.Contains(id)))
            {
                result.Add(userId);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<Guid>> GetRequiredTeamIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var memberships = await membershipQuery.GetUserTeamsAsync(userId, cancellationToken);
        var teamIds = memberships.Select(m => m.TeamId).ToList();

        // Volunteers docs apply to everyone.
        if (!teamIds.Contains(SystemTeamIds.Volunteers))
        {
            teamIds.Add(SystemTeamIds.Volunteers);
        }

        if (!teamIds.Contains(SystemTeamIds.Coordinators))
        {
            var isCoordinatorAnywhere = memberships.Any(m =>
                m.Role == TeamMemberRole.Coordinator &&
                m.TeamSystemTeamType == SystemTeamType.None);

            if (isCoordinatorAnywhere)
            {
                teamIds.Add(SystemTeamIds.Coordinators);
            }
        }

        return teamIds;
    }

    public async Task<MembershipPartition> PartitionUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var allIds = userIds.ToList();

        var usersById = await userService.GetUserInfosAsync(allIds, ct);

        var pendingDeletion = allIds
            .Where(id => usersById.TryGetValue(id, out var u) && u.DeletionRequestedAt != null)
            .ToHashSet();

        var remaining = allIds.Where(id => !pendingDeletion.Contains(id)).ToList();
        var incompleteSignup = new HashSet<Guid>();
        foreach (var id in remaining)
        {
            if (!usersById.TryGetValue(id, out var u) || u.Profile is null)
            {
                incompleteSignup.Add(id);
            }
        }

        remaining = remaining.Where(id => !incompleteSignup.Contains(id)).ToList();

        var suspended = new HashSet<Guid>();
        foreach (var id in remaining)
        {
            if (usersById[id].IsSuspended)
            {
                suspended.Add(id);
            }
        }

        remaining = remaining.Where(id => !suspended.Contains(id)).ToList();

        // Rejected → IncompleteSignup.
        var pendingApproval = new HashSet<Guid>();
        foreach (var id in remaining)
        {
            var profile = usersById[id].Profile!;
            if (!profile.IsApproved)
            {
                if (profile.RejectedAt is not null)
                {
                    incompleteSignup.Add(id);
                }
                else
                {
                    pendingApproval.Add(id);
                }
            }
        }

        remaining = remaining.Where(id => !pendingApproval.Contains(id) && !incompleteSignup.Contains(id)).ToList();

        var usersWithConsents = await GetUsersWithAllRequiredConsentsForTeamAsync(remaining, SystemTeamIds.Volunteers, ct);
        var active = remaining.Where(usersWithConsents.Contains).ToHashSet();
        var missingConsents = remaining.Where(id => !usersWithConsents.Contains(id)).ToHashSet();

        return new MembershipPartition(
            incompleteSignup,
            pendingApproval,
            active,
            missingConsents,
            suspended,
            pendingDeletion);
    }
}
