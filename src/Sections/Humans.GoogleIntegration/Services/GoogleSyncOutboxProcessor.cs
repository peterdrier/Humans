using Humans.Application.Architecture;
using Humans.GoogleIntegration.Contracts;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.GoogleIntegration.Data;
using Humans.Teams.Contracts;
using NodaTime;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// The Google sync outbox drain. Lifted verbatim out of
/// <c>Humans.Infrastructure/Jobs/ProcessGoogleSyncOutboxJob</c> at the section's G5 move:
/// the job injected this section's two repositories directly, both of which are now internal,
/// so the queue semantics moved in beside them and the job kept the Hangfire shim and its
/// job-level metric (G5-SECTION-TEMPLATE.md step 6b, <c>EmailOutboxProcessor</c>'s shape).
/// </summary>
/// <remarks>
/// SyncSettings enforcement is handled by the gateway methods in
/// <c>GoogleWorkspaceSyncService</c>, not here.
/// </remarks>
[CrossSectionWrite("Outbox processing writes Google email status back to the user.")]
internal sealed class GoogleSyncOutboxProcessor(
    IGoogleSyncOutboxRepository outboxRepository,
    IGoogleResourceRepository resourceRepository,
    IUserService userService,
    ITeamServiceRead teamService,
    IGoogleSyncService googleSyncService,
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

    public async Task ProcessQueuedAsync(CancellationToken cancellationToken = default)
    {
        var pendingEvents = await outboxRepository
            .GetProcessingBatchAsync(BatchSize, MaxRetryCount, cancellationToken);

        if (pendingEvents.Count == 0)
        {
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
                switch (outboxEvent.EventType)
                {
                    case GoogleSyncOutboxEventTypes.AddUserToTeamResources:
                        await googleSyncService.AddUserToTeamResourcesAsync(
                            outboxEvent.TeamId,
                            outboxEvent.UserId,
                            cancellationToken);
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

                await outboxRepository.MarkProcessedAsync(
                    outboxEvent.Id, clock.GetCurrentInstant(), cancellationToken);
                metrics.RecordSyncOperation("success");

                // Only mark user as Valid when the event actually touched Google APIs
                // (AddUserToTeamResources with linked resources). RemoveUserFromTeamResources
                // is a no-op, and Add with zero resources doesn't validate the email.
                if (string.Equals(outboxEvent.EventType, GoogleSyncOutboxEventTypes.AddUserToTeamResources, StringComparison.Ordinal))
                {
                    var activeResources = await resourceRepository
                        .GetActiveByTeamIdAsync(outboxEvent.TeamId, cancellationToken);
                    if (activeResources.Count > 0)
                    {
                        await userService.TrySetGoogleEmailStatusFromSyncAsync(
                            outboxEvent.UserId, GoogleEmailStatus.Valid, cancellationToken);
                    }
                }
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
                    outboxEvent.UserId, GoogleEmailStatus.Rejected, cancellationToken);

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
