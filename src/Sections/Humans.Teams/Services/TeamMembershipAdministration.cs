using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamMembershipAdministration(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<TeamService> logger,
    IServiceProvider serviceProvider,
    TeamSnapshotReader snapshotReader,
    TeamMembershipOutbox membershipOutbox,
    TeamMembershipNotices membershipNotices) : IApplicationService
{
    private ISystemTeamSync SystemTeamSync => serviceProvider.GetRequiredService<ISystemTeamSync>();

    internal async Task<TeamMembershipMutationResult> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamMembershipMutationResult(false, "Teams_NotFound");

        if (team.IsSystemTeam)
            return new TeamMembershipMutationResult(false, "Teams_CannotLeaveSystem");

        var member = await repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            return new TeamMembershipMutationResult(false, "Teams_NotMember");

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        await membershipOutbox.MarkMemberLeftWithOutboxAsync(member, null, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamLeft, nameof(Team), teamId,
            $"Left {team.Name}",
            userId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        if (wasCoordinator)
        {
            await SystemTeamSync.SyncMembershipForUserAsync(
                userId, SystemTeamType.Coordinators, cancellationToken);
        }

        return new TeamMembershipMutationResult(true, WasCoordinator: wasCoordinator);
    }

    internal Task<TeamMemberRemovalResult> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        RemoveMemberCoreAsync(teamId, userId, actorUserId, null, cancellationToken);

    private async Task<TeamMemberRemovalResult> RemoveMemberCoreAsync(
        Guid teamId, Guid userId, Guid actorUserId, Guid? mergeTargetUserId, CancellationToken cancellationToken)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_TeamMissing", $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_SystemTeam", "Cannot remove members from system team manually");

        var canApprove = await snapshotReader.CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_Permission", "User does not have permission to remove members from this team");

        var member = await repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_NotMember", "User is not a member of this team");

        TeamMember? mergeTarget = null;
        if (mergeTargetUserId is { } targetUserId)
        {
            if (targetUserId == userId)
                throw new InvalidOperationException("Cannot merge a membership into itself");
            mergeTarget = await repo.FindActiveMemberForMutationAsync(teamId, targetUserId, cancellationToken);
            if (mergeTarget is null)
            {
                var addition = await AddMemberToTeamAsync(teamId, targetUserId, actorUserId, cancellationToken);
                mergeTarget = addition.Succeeded ? addition.Member! : throw new InvalidOperationException(addition.Refusal);
            }
        }
        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        var removedAssignments = await membershipOutbox.MarkMemberLeftWithOutboxAsync(member, mergeTarget?.Id, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberRemoved, nameof(Team), teamId,
            $"Member removed from {team.Name}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        if (mergeTarget is not null && (wasCoordinator || removedAssignments.Count > 0))
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberRoleChanged, nameof(Team), teamId,
                $"Folded {removedAssignments.Count} role assignment(s) from merged account {userId}; source coordinator: {wasCoordinator}",
                actorUserId, mergeTarget.UserId, AuditSubjectTypes.User);
        }

        await membershipNotices.SendMemberRemovedNotificationAsync(team, userId, teamId, cancellationToken);

        // A removed coordinator's Coordinators eligibility is reconciled in the mutation, not left to the caller.
        if (wasCoordinator && mergeTarget is null)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
        return new TeamMemberRemovalResult();
    }

    private TeamMemberRemovalResult RefuseMemberRemoval(Guid teamId, Guid userId, Guid actorId, string key, string message)
    {
        logger.LogWarning("Member removal refused for {UserId} from team {TeamId} by {ActorId}: {ErrorKey}", userId, teamId, actorId, key);
        return new TeamMemberRemovalResult(key, message);
    }

    internal async Task<TeamJoinReviewResult> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return new(Refusal: "Cannot add members to system teams manually");

        var resolvedUserId = await snapshotReader.ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        if (resolvedUserId is null)
            return new(Refusal: TeamSnapshotReader.UnavailableMembershipTargetMessage);
        targetUserId = resolvedUserId.Value;
        var isExisting = await repo.IsActiveMemberAsync(teamId, targetUserId, cancellationToken);
        if (isExisting)
            return new(Refusal: "User is already a member of this team");

        // A pending request is resolved by approving it with the membership.
        var pendingRequest = await repo.FindUserPendingRequestAsync(teamId, targetUserId, cancellationToken);
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = targetUserId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await membershipOutbox.BuildOutboxEventAsync(
            member.Id, teamId, targetUserId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            if (pendingRequest is not null)
            {
                var tracked = await repo.FindRequestForMutationAsync(pendingRequest.Id, cancellationToken);
                if (tracked is not null)
                {
                    if (tracked.Status == TeamJoinRequestStatus.Pending)
                    {
                        tracked.Status = TeamJoinRequestStatus.Approved;
                        tracked.ReviewedByUserId = actorUserId;
                        tracked.ReviewNotes = "Added directly by team manager";
                        tracked.ResolvedAt = clock.GetCurrentInstant();
                    }
                    success = await membershipOutbox.ApproveRequestWithMemberAndOutboxAsync(
                        tracked, member, outboxEvent, cancellationToken);
                }
                else
                {
                    success = await membershipOutbox.TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
                }
            }
            else
            {
                success = await membershipOutbox.TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId}", targetUserId, teamId);
            throw;
        }

        if (!success)
            return new(Refusal: "User is already a member of this team");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberAdded, nameof(Team), teamId,
            $"Member added to {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);

        await membershipNotices.SendAddedToTeamEmailAsync(targetUserId, team, cancellationToken);

        return new(Member: member);
    }

    internal async Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var newRole = await repo.SetMemberRoleAsync(teamId, userId, role, cancellationToken);
        if (newRole is null)
            return;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberRoleChanged, nameof(Team), teamId,
            $"Set member role to {role}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
    }

    internal Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repo.RevokeAllMembershipsAsync(userId, clock.GetCurrentInstant(), cancellationToken);

    internal async Task ReassignAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken cancellationToken)
    {
        // Ends source memberships through the ordinary removal/outbox path, folding roles into the survivor in the
        // same save. System teams reconcile separately.
        var sourceMemberships = await snapshotReader.GetUserTeamMembershipsAsync(sourceUserId, cancellationToken);
        var sourceTeamsById = await repo.GetByIdsWithParentsAsync(
            sourceMemberships.Select(m => m.TeamId).Distinct().ToList(), cancellationToken);

        foreach (var membership in sourceMemberships)
        {
            if (sourceTeamsById.TryGetValue(membership.TeamId, out var team) && team.IsSystemTeam)
                continue;

            var removal = await RemoveMemberCoreAsync(
                membership.TeamId, sourceUserId, actorUserId, targetUserId, cancellationToken);
            if (removal.ErrorKey is not null)
                throw new InvalidOperationException(removal.ErrorMessage);
        }

        await repo.ReassignActiveJoinRequestsAsync(sourceUserId, targetUserId, cancellationToken);

        await repo.ReassignEarlyEntryGrantsAsync(sourceUserId, targetUserId, cancellationToken);
        // Always retry both reconciles, even when an earlier pass ended every source membership before failing here.
        await SystemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.Coordinators, cancellationToken);
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
    }

}
