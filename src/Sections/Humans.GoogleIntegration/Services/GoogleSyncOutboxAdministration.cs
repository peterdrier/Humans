using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.GoogleIntegration.Services;

internal sealed class GoogleSyncOutboxAdministration(
    IGoogleSyncOutboxRepository googleSyncOutboxRepository,
    IWorkspaceIdentityManagement workspaceIdentities,
    ITeamService teamService,
    IClock clock,
    IAuditLogService auditLogService,
    ILogger logger) : IApplicationService
{
    public Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default)
        => googleSyncOutboxRepository.CountFailedAsync(cancellationToken);

    public Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default)
        => googleSyncOutboxRepository.CountPendingAsync(cancellationToken);

    public async Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var events = await googleSyncOutboxRepository.GetRecentAsync(take, cancellationToken);
        return events
            .Select(e => new GoogleSyncOutboxEventSnapshot(
                e.Id,
                e.EventType,
                e.TeamId,
                e.UserId,
                e.OccurredAt,
                e.ProcessedAt,
                e.RetryCount,
                e.LastError,
                e.FailedPermanently))
            .ToList();
    }

    public async Task<bool> RequeueOutboxEventAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var requeued = await googleSyncOutboxRepository.RequeueAsync(id, cancellationToken);
        if (requeued)
            await AuditOutboxRequeueAsync(auditLogService, id, actorUserId);
        return requeued;
    }

    public async Task<int> RequeueAllFailedOutboxEventsAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var count = await googleSyncOutboxRepository.RequeueAllFailedAsync(cancellationToken);
        await AuditRequeueAllAsync(auditLogService, count, actorUserId);
        return count;
    }

    public async Task<int> EnqueueUserSyncAsync(Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var count = await EnqueueUserSyncEventsAsync(userId, cancellationToken);
        await AuditUserSyncRerunAsync(auditLogService, userId, count, actorUserId);
        return count;
    }

    // Admin retry audits, shared with StubGoogleSyncService so both modes leave the same trail.
    internal static Task AuditOutboxRequeueAsync(IAuditLogService auditLog, Guid id, Guid actorUserId) =>
        auditLog.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            "GoogleSyncOutboxEvent",
            id,
            $"Admin manually requeued Google sync outbox event {id}",
            actorUserId);

    internal static Task AuditRequeueAllAsync(IAuditLogService auditLog, int count, Guid actorUserId) =>
        auditLog.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            "GoogleSyncOutbox",
            Guid.Empty,
            $"Admin requeued all {count} permanently-failed Google sync outbox event(s)",
            actorUserId);

    internal static Task AuditUserSyncRerunAsync(IAuditLogService auditLog, Guid userId, int count, Guid actorUserId) =>
        auditLog.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            AuditSubjectTypes.User,
            userId,
            $"Admin enqueued Google sync re-run for user {userId} ({count} team event(s))",
            actorUserId);

    private async Task<int> EnqueueUserSyncEventsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var identity = await workspaceIdentities.GetWorkspaceIdentityAsync(userId, cancellationToken);

        // AddUserToTeamResourcesAsync would skip every event for a Rejected identity, so enqueue none.
        if (identity?.Status == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug("EnqueueUserSyncAsync skipped for user {UserId} — Google identity is Rejected", userId);
            return 0;
        }

        var now = clock.GetCurrentInstant();
        var memberships = await teamService.GetUserTeamMembershipsAsync(userId, cancellationToken);
        var events = memberships
            .Select(t => new GoogleSyncOutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                TeamId = t.TeamId,
                UserId = userId,
                OccurredAt = now,
                DeduplicationKey = $"admin-resync:{userId}:{t.TeamId}:{now.ToUnixTimeTicks()}"
            })
            .ToList();

        if (events.Count > 0)
            await googleSyncOutboxRepository.AddRangeAsync(events, cancellationToken);

        return events.Count;
    }
}
