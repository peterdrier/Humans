using Hangfire;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains queued Google sync outbox events and executes the underlying sync operations.
/// SyncSettings enforcement is handled by the gateway methods in GoogleWorkspaceSyncService.
/// </summary>
/// <remarks>
/// §15 Part 2c (issue #576): the job no longer injects <c>HumansDbContext</c>.
/// Outbox reads/writes go through <see cref="IGoogleSyncOutboxRepository"/>,
/// the "are there any active Drive/Group resources for this team?" check
/// goes through <see cref="IGoogleResourceRepository"/>, and the
/// user-GoogleEmailStatus mutation goes through <see cref="IUserService"/>.
/// User/team display-name lookups for the error log go through
/// <see cref="IUserService.GetByIdsAsync"/> and
/// <see cref="ITeamServiceRead.GetTeamsAsync"/>.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ProcessGoogleSyncOutboxJob(
    IGoogleSyncOutboxRepository outboxRepository,
    IGoogleResourceRepository resourceRepository,
    IUserService userService,
    ITeamServiceRead teamService,
    IGoogleSyncService googleSyncService,
    INotificationService notificationService,
    IHumansMetrics metrics,
    IClock clock,
    ILogger<ProcessGoogleSyncOutboxJob> logger) : IRecurringJob
{
    private const int BatchSize = 100;
    private const int MaxRetryCount = 10;

    /// <summary>
    /// HTTP status codes that indicate a permanent user-level failure (do not retry).
    /// 400 = bad request (invalid email format), 403 = email domain ineligible for
    /// Google Groups (e.g., proton.me), 404 = user not found.
    /// </summary>
    private static readonly HashSet<int> PermanentErrorCodes = [400, 403, 404];

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
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

                    // Notify Admin so the failure is visible immediately
                    // (badge fix nobodies-collective/Humans#847 also surfaces this in the meter).
                    try
                    {
                        await notificationService.SendToRoleAsync(
                            NotificationSource.SyncError,
                            NotificationClass.Actionable,
                            NotificationPriority.High,
                            $"Google sync permanently failed for {userEmail}",
                            RoleNames.Admin,
                            body: $"Event {outboxEvent.EventType} for team {teamName} failed with HTTP {ex.Error?.Code}: {ex.Message}",
                            actionUrl: "/Google/SyncOutbox",
                            actionLabel: "View outbox",
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception notifyEx)
                    {
                        logger.LogError(notifyEx,
                            "Failed to send Admin notification for permanent outbox failure {OutboxId}", outboxEvent.Id);
                    }
                }
                catch (Exception ex)
                {
                    metrics.RecordSyncOperation("failure");

                    var (exhausted, retryCount) = await outboxRepository.IncrementRetryAsync(
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

                    // Notify Admin when retry budget is exhausted (dead-lettered by retry limit).
                    if (exhausted)
                    {
                        try
                        {
                            await notificationService.SendToRoleAsync(
                                NotificationSource.SyncError,
                                NotificationClass.Actionable,
                                NotificationPriority.High,
                                $"Google sync dead-lettered for {userEmail} (retry limit reached)",
                                RoleNames.Admin,
                                body: $"Event {outboxEvent.EventType} for team {teamName} exhausted {MaxRetryCount} retries. Last error: {ex.Message}",
                                actionUrl: "/Google/SyncOutbox",
                                actionLabel: "View outbox",
                                cancellationToken: cancellationToken);
                        }
                        catch (Exception notifyEx)
                        {
                            logger.LogError(notifyEx,
                                "Failed to send Admin notification for dead-lettered outbox event {OutboxId}", outboxEvent.Id);
                        }
                    }
                }
            }

            metrics.RecordJobRun("process_google_sync_outbox", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("process_google_sync_outbox", "failure");
            logger.LogError(ex, "Error processing Google sync outbox");
            throw;
        }
    }
}
