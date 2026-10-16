using Humans.Base.Attributes;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// The Google sync outbox drain. <c>ProcessGoogleSyncOutboxJob</c> is the Hangfire shim around
/// it and owns the job-level metric.
/// </summary>
/// <remarks>
/// SyncSettings enforcement is handled by the gateway methods in
/// <c>GoogleWorkspaceSyncService</c>, not here.
/// </remarks>
[CrossSectionWrite("Outbox processing writes Google email status back to the user.")]
internal sealed class GoogleSyncOutboxProcessor(
    IGoogleSyncOutboxRepository outboxRepository,
    IUserService userService,
    ITeamServiceRead teamService,
    IGoogleSyncService googleSyncService,
    IGoogleDriveActivityClient googleClient,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<GoogleSyncOutboxProcessor> logger) : IGoogleSyncOutboxProcessor
{
    private const int BatchSize = 100;
    private const int MaxRetryCount = 10;

    /// <summary>
    /// HTTP status codes that indicate a permanent user-level failure (do not retry).
    /// 400 = bad request (invalid email format), 403 = email domain ineligible for
    /// Google Groups (e.g., proton.me), 404 = user not found.
    /// </summary>
    private static readonly HashSet<int> PermanentErrorCodes = [400, 403, 404];

    /// <summary>
    /// Admin-initiated reruns carry one of two dedup-key shapes: the per-human rerun
    /// (<c>admin-resync:{userId}:{teamId}:{ticks}</c>, <c>EnqueueUserSyncAsync</c>) and the
    /// account-link resync (<c>{teamMemberId}:AddUserToTeamResources:resync:{instant}</c>,
    /// <c>ITeamService.EnqueueGoogleResyncForUserTeamsAsync</c>). Ordinary joins are
    /// <c>{teamMemberId}:{eventType}</c>.
    /// </summary>
    internal static bool IsManualResync(string deduplicationKey)
        => deduplicationKey.StartsWith("admin-resync:", StringComparison.Ordinal)
           || deduplicationKey.Contains(":resync:", StringComparison.Ordinal);

    public async Task ProcessQueuedAsync(CancellationToken cancellationToken = default)
    {
        var pendingEvents = await outboxRepository
            .GetProcessingBatchAsync(BatchSize, MaxRetryCount, cancellationToken);

        if (pendingEvents.Count == 0)
        {
            return;
        }

        if (!googleClient.IsConfigured)
        {
            logger.LogWarning(
                "Skipping {Count} Google sync outbox event(s) because Google Workspace is not configured; events remain pending",
                pendingEvents.Count);
            return;
        }

        var userIds = pendingEvents.Select(e => e.UserId).Distinct().ToList();
        var teamIds = pendingEvents.Select(e => e.TeamId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, cancellationToken);
        var userEmailLookup = users.ToDictionary(
            kvp => kvp.Key, kvp => kvp.Value.Email ?? "unknown");
        // An event queued for a since-merged id was carried out for its survivor (the sync
        // service resolves), so the Google email status lands on the survivor too.
        Guid ResolvedUserId(Guid id) => users.TryGetValue(id, out var info) ? info.Id : id;
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var teamNameLookup = teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        foreach (var outboxEvent in pendingEvents)
        {
            try
            {
                var grantOutcome = GoogleResourceGrantOutcome.Deferred;
                switch (outboxEvent.EventType)
                {
                    case GoogleSyncOutboxEventTypes.AddUserToTeamResources:
                        var syncSource = IsManualResync(outboxEvent.DeduplicationKey)
                            ? GoogleSyncSource.ManualSync
                            : GoogleSyncSource.TeamMemberJoined;
                        grantOutcome = await googleSyncService.AddUserToTeamResourcesAsync(
                            outboxEvent.TeamId,
                            outboxEvent.UserId,
                            cancellationToken,
                            syncSource);
                        break;

                    case GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources:
                        await googleSyncService.RemoveUserFromTeamResourcesAsync(
                            outboxEvent.TeamId,
                            outboxEvent.UserId,
                            cancellationToken);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown outbox event type '{outboxEvent.EventType}'.");
                }

                // Linked resources and queued group sync do not prove vendor acceptance.
                if (grantOutcome == GoogleResourceGrantOutcome.Accepted)
                {
                    await userService.TrySetGoogleEmailStatusFromSyncAsync(
                        ResolvedUserId(outboxEvent.UserId), GoogleEmailStatus.Valid, cancellationToken);
                }

                // Finish the status tail before closing the event: its failure
                // must remain eligible for the retry path below.
                await outboxRepository.MarkProcessedAsync(
                    outboxEvent.Id, clock.GetCurrentInstant(), cancellationToken);
                metrics.RecordSyncOperation("success");
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code is int code && PermanentErrorCodes.Contains(code))
            {
                metrics.RecordSyncOperation("permanent_failure");

                await outboxRepository.MarkPermanentlyFailedAsync(
                    outboxEvent.Id, clock.GetCurrentInstant(), ex.Message, cancellationToken);

                var userEmail = userEmailLookup.GetValueOrDefault(outboxEvent.UserId, "unknown");
                var teamName = teamNameLookup.GetValueOrDefault(outboxEvent.TeamId, outboxEvent.TeamId.ToString());

                logger.LogWarning(
                    ex,
                    "Permanent failure processing Google sync outbox event {OutboxId} ({EventType}) for user {UserEmail} in team {TeamName} — HTTP {StatusCode}, not retrying",
                    outboxEvent.Id,
                    outboxEvent.EventType,
                    userEmail,
                    teamName,
                    ex.Error?.Code);

                await userService.TrySetGoogleEmailStatusFromSyncAsync(
                    ResolvedUserId(outboxEvent.UserId), GoogleEmailStatus.Rejected, cancellationToken);

                // Failure stays visible via the "Failed Google sync events" meter and
                // the /Google/SyncOutbox admin page (with per-event Retry) — no per-event
                // notification (removed: the alert was non-actionable noise).
            }
            catch (Exception ex)
            {
                metrics.RecordSyncOperation("failure");

                var (_, retryCount) = await outboxRepository.IncrementRetryAsync(
                    outboxEvent.Id,
                    clock.GetCurrentInstant(),
                    ex.Message,
                    MaxRetryCount,
                    cancellationToken);

                var userEmail = userEmailLookup.GetValueOrDefault(outboxEvent.UserId, "unknown");
                var teamName = teamNameLookup.GetValueOrDefault(outboxEvent.TeamId, outboxEvent.TeamId.ToString());

                logger.LogError(
                    ex,
                    "Failed processing Google sync outbox event {OutboxId} ({EventType}) for user {UserEmail} in team {TeamName} — attempt {Attempt}/{MaxRetries}",
                    outboxEvent.Id,
                    outboxEvent.EventType,
                    userEmail,
                    teamName,
                    retryCount,
                    MaxRetryCount);

                // Dead-lettered events (IncrementRetryAsync marks FailedPermanently on
                // exhaustion) surface via the "Failed Google sync events" meter and the
                // /Google/SyncOutbox admin page with Retry — no per-event notification.
            }
        }
    }
}
