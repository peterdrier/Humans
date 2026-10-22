using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>SyncSettings enforcement lives in the <c>GoogleWorkspaceSyncService</c> gateway methods, not here.</summary>
internal sealed class GoogleSyncOutboxProcessor(
    IGoogleSyncOutboxRepository outboxRepository,
    IUserService userService,
    IWorkspaceIdentityManagement workspaceIdentities,
    ITeamService teamService,
    IGoogleSyncManagement googleSyncService,
    IGoogleDriveActivityClient googleClient,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<GoogleSyncOutboxProcessor> logger) : IGoogleSyncOutboxProcessor
{
    private const int BatchSize = 100;
    private const int MaxRetryCount = 10;

    /// <summary>
    /// Manual reruns use <c>admin-resync:{userId}:{teamId}:{ticks}</c> or
    /// <c>{teamMemberId}:AddUserToTeamResources:resync:{instant}</c>; ordinary joins use <c>{teamMemberId}:{eventType}</c>.
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
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var teamNameLookup = teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        foreach (var outboxEvent in pendingEvents)
        {
            try
            {
                var grant = new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Deferred, outboxEvent.UserId);
                switch (outboxEvent.EventType)
                {
                    case GoogleSyncOutboxEventTypes.AddUserToTeamResources:
                        var syncSource = IsManualResync(outboxEvent.DeduplicationKey)
                            ? GoogleSyncSource.ManualSync
                            : GoogleSyncSource.TeamMemberJoined;
                        grant = await googleSyncService.AddUserToTeamResourcesAsync(
                            outboxEvent.TeamId,
                            outboxEvent.UserId,
                            cancellationToken,
                            syncSource);
                        break;

                    case GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources:
                        // Nothing to do per event: reconciliation removes access the member no longer holds.
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown outbox event type '{outboxEvent.EventType}'.");
                }

                if (grant.Outcome == GoogleResourceGrantOutcome.Failed)
                {
                    var error = grant.ErrorMessage ?? "Google resource provisioning failed";
                    if (grant.Retryable)
                        throw new InvalidOperationException(error);
                    await outboxRepository.MarkPermanentlyFailedAsync(
                        outboxEvent.Id, clock.GetCurrentInstant(), error, cancellationToken);
                    metrics.RecordSyncOperation("permanent_failure");
                    logger.LogWarning("Google provisioning refused outbox event {OutboxId}: {Error}", outboxEvent.Id, error);
                    continue;
                }
                // Acceptance identifies the address Google saw, rather than whichever address
                // happens to be selected after the asynchronous call completes.
                if (grant.Outcome == GoogleResourceGrantOutcome.Accepted)
                {
                    if (grant.GoogleEmail is not { } attempted)
                        throw new InvalidOperationException("Accepted Google grant omitted its target address");
                    await workspaceIdentities.SetStatusFromSyncAsync(
                        grant.UserId, attempted, GoogleEmailStatus.Valid, cancellationToken);
                }

                // Finish the status tail before closing the event: its failure
                // must remain eligible for the retry path below.
                await outboxRepository.MarkProcessedAsync(
                    outboxEvent.Id, clock.GetCurrentInstant(), cancellationToken);
                metrics.RecordSyncOperation("success");
            }
            catch (Google.GoogleApiException ex) when (ex.Error?.Code is int code && DrivePermissionErrorPolicy.IsPermanentFailure(code))
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

                // An escaping exception supplies no attempted email row, so no guessed address is rejected.
                // Failures show on the "Failed Google sync events" meter and /Google/SyncOutbox.
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

                // Dead-lettered events show on the meter and /Google/SyncOutbox; no per-event notification.
            }
        }
    }
}
