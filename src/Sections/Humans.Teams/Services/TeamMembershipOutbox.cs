using System.Transactions;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamMembershipOutbox(
    ITeamRepository repo,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<TeamService> logger) : IApplicationService
{
    // Lazy: GoogleIntegration's identity read resolves Users, which depends on Teams.
    private IGoogleIntegrationService GoogleIdentities
        => serviceProvider.GetRequiredService<IGoogleIntegrationService>();

    private IGoogleSyncOutboxService GoogleSyncOutboxService
        => serviceProvider.GetRequiredService<IGoogleSyncOutboxService>();

    internal async Task<bool> TryAddMemberWithOutboxAsync(
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct)
    {
        if (outboxEvent is null)
            return await repo.TryAddMemberAsync(member, ct);

        using var scope = CreateTransactionScope();
        var success = await repo.TryAddMemberAsync(member, ct);
        if (!success)
            return false;

        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return true;
    }

    internal async Task<bool> ApproveRequestWithMemberAndOutboxAsync(
        TeamJoinRequest request,
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct)
    {
        if (outboxEvent is null)
            return await repo.ApproveRequestWithMemberAsync(request, member, ct);

        using var scope = CreateTransactionScope();
        var success = await repo.ApproveRequestWithMemberAsync(request, member, ct);
        if (!success)
            return false;

        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return true;
    }

    internal async Task<IReadOnlyList<TeamRoleAssignment>> MarkMemberLeftWithOutboxAsync(
        TeamMember member,
        Guid? mergeIntoMemberId,
        CancellationToken ct)
    {
        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, member.TeamId, member.UserId, GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources, ct);
        var leftAt = clock.GetCurrentInstant();
        if (outboxEvent is null)
            return await repo.MarkMemberLeftAsync(member.Id, leftAt, mergeIntoMemberId, ct);

        using var scope = CreateTransactionScope();
        var removedAssignments = await repo.MarkMemberLeftAsync(member.Id, leftAt, mergeIntoMemberId, ct);
        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return removedAssignments;
    }

    internal async Task<TeamRoleAssignmentMutationResult> AssignToRoleWithOutboxAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        TeamMember? autoAddMember,
        GoogleSyncOutboxEvent? outboxEvent,
        bool promoteToCoordinator,
        Instant now,
        CancellationToken ct)
    {
        if (outboxEvent is null)
        {
            return await repo.AssignToRoleAsync(
                roleDefinitionId,
                targetUserId,
                actorUserId,
                autoAddMember,
                promoteToCoordinator,
                now,
                ct);
        }

        using var scope = CreateTransactionScope();
        var result = await repo.AssignToRoleAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            promoteToCoordinator,
            now,
            ct);

        if (!result.Succeeded)
            return result;
        if (autoAddMember is not null)
            await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);

        scope.Complete();
        return result;
    }

    private static TransactionScope CreateTransactionScope() =>
        new(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);

    internal async Task<GoogleSyncOutboxEvent?> BuildOutboxEventAsync(
        Guid teamMemberId,
        Guid teamId,
        Guid userId,
        string eventType,
        CancellationToken ct)
    {
        var identity = await GoogleIdentities.GetWorkspaceIdentityAsync(userId, ct);
        if (identity?.Status == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug(
                "Skipping Google sync outbox event {EventType} for user {UserId} — Google identity is Rejected",
                eventType, userId);
            return null;
        }

        return new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = teamId,
            UserId = userId,
            OccurredAt = clock.GetCurrentInstant(),
            DeduplicationKey = $"{teamMemberId}:{eventType}"
        };
    }

}
