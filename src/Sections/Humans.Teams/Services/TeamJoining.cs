using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamJoining(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<TeamService> logger,
    TeamSnapshotReader snapshotReader,
    TeamMembershipOutbox membershipOutbox,
    TeamMembershipNotices membershipNotices) : IApplicationService
{
    private static readonly TrackedLock JoinRequestLock = new("Teams.JoinRequest");

    internal async Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamJoinResult(ErrorKey: "Teams_NotFound");

        if (team.RequiresApproval)
        {
            var result = await RequestToJoinTeamAsync(teamId, userId, message, cancellationToken);
            return result.ErrorKey is null
                ? new TeamJoinResult(TeamJoinOutcome.RequestSubmitted)
                : new TeamJoinResult(ErrorKey: result.ErrorKey);
        }

        var directResult = await JoinTeamDirectlyAsync(teamId, userId, cancellationToken);
        return directResult.ErrorKey is null
            ? new TeamJoinResult(TeamJoinOutcome.Joined)
            : new TeamJoinResult(ErrorKey: directResult.ErrorKey);
    }

    private static string? SelfJoinRefusal(Team team, bool requiresApproval)
    {
        if (!team.IsActive)
            return "Teams_NotFound";
        if (team.IsSystemTeam)
            return "Teams_Team_CannotJoinSystem";
        if (team.IsHidden)
            return "Teams_CannotJoinHidden";
        return team.RequiresApproval != requiresApproval ? "Teams_JoinPolicyChanged" : null;
    }

    internal async Task<TeamJoinRequestResult> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamJoinRequestResult(ErrorKey: "Teams_NotFound");

        if (SelfJoinRefusal(team, requiresApproval: true) is { } errorKey)
            return new TeamJoinRequestResult(ErrorKey: errorKey);

        using var joinRequest = await JoinRequestLock.AcquireAsync(logger, cancellationToken);
        var existingRequest = await repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);
        if (existingRequest is not null)
            return new TeamJoinRequestResult(ErrorKey: "Teams_Team_AlreadyPendingRequest");

        var teamInfo = await snapshotReader.GetTeamAsync(teamId, cancellationToken);
        var isMember = teamInfo is { IsActive: true } && teamInfo.Members.Any(m => m.UserId == userId);
        if (isMember)
            return new TeamJoinRequestResult(ErrorKey: "Teams_Team_AlreadyMember");

        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Message = message,
            RequestedAt = clock.GetCurrentInstant()
        };

        await repo.AddRequestAsync(request, cancellationToken);
        joinRequest.Dispose();

        // The join request is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        logger.LogInformation("User {UserId} requested to join team {TeamId}", userId, teamId);
        await membershipNotices.SendTeamJoinRequestSubmittedNotificationAsync(team, userId, cancellationToken);

        return new TeamJoinRequestResult(request);
    }

    internal async Task<TeamDirectJoinResult> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamDirectJoinResult(ErrorKey: "Teams_NotFound");

        if (SelfJoinRefusal(team, requiresApproval: false) is { } errorKey)
            return new TeamDirectJoinResult(ErrorKey: errorKey);

        var existingMember = await repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
        if (existingMember)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_AlreadyMember");

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await membershipOutbox.BuildOutboxEventAsync(
            member.Id, teamId, userId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            success = await membershipOutbox.TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId} directly", userId, teamId);
            throw;
        }

        if (!success)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_AlreadyMember");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamJoinedDirectly, nameof(Team), teamId,
            $"Joined {team.Name} directly",
            userId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        await membershipNotices.SendAddedToTeamEmailAsync(userId, team, cancellationToken);
        await membershipNotices.SendTeamMemberAddedNotificationAsync(team, userId, cancellationToken);

        return new TeamDirectJoinResult(member);
    }

    internal async Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var withdrew = await repo.WithdrawRequestAsync(requestId, userId, now, cancellationToken);
        if (!withdrew)
            return new TeamMembershipMutationResult(false, "Teams_RequestUnavailable");


        logger.LogInformation("User {UserId} withdrew join request {RequestId}", userId, requestId);
        return new TeamMembershipMutationResult(true);
    }

    internal async Task<TeamJoinReviewResult> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken);
        if (request is null)
            return new(Refusal: "Join request not found");

        var canApprove = await snapshotReader.CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            return new(Refusal: "User does not have permission to approve requests for this team");

        if (request.Status != TeamJoinRequestStatus.Pending)
            return new(Refusal: "Can only approve pending requests");
        request.Status = TeamJoinRequestStatus.Approved;
        request.ReviewedByUserId = approverUserId;
        request.ReviewNotes = notes;
        var now = clock.GetCurrentInstant();
        request.ResolvedAt = now;

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = request.TeamId,
            UserId = request.UserId,
            Role = TeamMemberRole.Member,
            JoinedAt = now
        };

        var outboxEvent = await membershipOutbox.BuildOutboxEventAsync(
            member.Id, request.TeamId, request.UserId,
            GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            success = await membershipOutbox.ApproveRequestWithMemberAndOutboxAsync(
                request, member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to approve join request {RequestId} for user {UserId} to team {TeamId}",
                requestId, request.UserId, request.TeamId);
            throw;
        }

        if (!success)
            return new(Refusal: "User is already a member of this team");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamJoinRequestApproved, nameof(Team), request.TeamId,
            $"Join request for {request.Team.Name} approved",
            approverUserId,
            relatedEntityId: request.UserId, relatedEntityType: AuditSubjectTypes.User);


        await membershipNotices.SendAddedToTeamEmailAsync(request.UserId, request.Team, cancellationToken);
        await membershipNotices.SendJoinRequestApprovedNotificationAsync(request.Team, member.UserId, cancellationToken);

        return new(Member: member);
    }

    internal async Task<TeamJoinReviewResult> RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken);
        if (request is null)
            return new(Refusal: "Join request not found");

        var canApprove = await snapshotReader.CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            return new(Refusal: "User does not have permission to reject requests for this team");

        var now = clock.GetCurrentInstant();
        var rejected = await repo.RejectRequestAsync(requestId, approverUserId, reason, now, cancellationToken);
        if (!rejected)
            return new(Refusal: "Can only reject pending requests");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamJoinRequestRejected, nameof(Team), request.TeamId,
            $"Join request for team rejected: {reason}",
            approverUserId,
            relatedEntityId: request.UserId, relatedEntityType: AuditSubjectTypes.User);

        await membershipNotices.SendJoinRequestRejectedNotificationAsync(request.Team, request.UserId, cancellationToken);
        return new();
    }

}
