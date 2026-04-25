using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Metering;
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
/// <see cref="ITeamService.GetTeamNamesByIdsAsync"/>.
/// </remarks>
public class ProcessGoogleSyncOutboxJob : IRecurringJob
{
    private const int BatchSize = 100;
    private const int MaxRetryCount = 10;

    /// <summary>
    /// HTTP status codes that indicate a permanent user-level failure (do not retry).
    /// 400 = bad request (invalid email format), 403 = email domain ineligible for
    /// Google Groups (e.g., proton.me), 404 = user not found.
    /// </summary>
    private static readonly HashSet<int> PermanentErrorCodes = [400, 403, 404];

    private readonly IGoogleSyncOutboxRepository _outboxRepository;
    private readonly IGoogleResourceRepository _resourceRepository;
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly INotificationService _notificationService;
    private readonly Counter<long> _syncOperationsCounter;
    private readonly Counter<long> _jobRunsCounter;
    private readonly IClock _clock;
    private readonly ILogger<ProcessGoogleSyncOutboxJob> _logger;

    public ProcessGoogleSyncOutboxJob(
        IGoogleSyncOutboxRepository outboxRepository,
        IGoogleResourceRepository resourceRepository,
        IUserService userService,
        ITeamService teamService,
        IGoogleSyncService googleSyncService,
        INotificationService notificationService,
        IMeters meters,
        IClock clock,
        ILogger<ProcessGoogleSyncOutboxJob> logger)
    {
        _outboxRepository = outboxRepository;
        _resourceRepository = resourceRepository;
        _userService = userService;
        _teamService = teamService;
        _googleSyncService = googleSyncService;
        _notificationService = notificationService;
        _syncOperationsCounter = meters.RegisterCounter(
            "humans.sync_operations_total",
            new MeterMetadata("Total Google sync operations", "{operations}"));
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pendingEvents = await _outboxRepository
                .GetProcessingBatchAsync(BatchSize, MaxRetryCount, cancellationToken);

            if (pendingEvents.Count == 0)
            {
                return;
            }

            // Pre-load contextual info for richer error messages
            var userIds = pendingEvents.Select(e => e.UserId).Distinct().ToList();
            var teamIds = pendingEvents.Select(e => e.TeamId).Distinct().ToList();
            var users = await _userService.GetByIdsAsync(userIds, cancellationToken);
            var userEmailLookup = users.ToDictionary(
                kvp => kvp.Key, kvp => kvp.Value.Email ?? "unknown");
            var teamNameLookup = await _teamService.GetTeamNamesByIdsAsync(
                teamIds, cancellationToken);

            foreach (var outboxEvent in pendingEvents)
            {
                try
                {
                    switch (outboxEvent.EventType)
                    {
                        case GoogleSyncOutboxEventTypes.AddUserToTeamResources:
                            await _googleSyncService.AddUserToTeamResourcesAsync(
                                outboxEvent.TeamId,
                                outboxEvent.UserId,
                                cancellationToken);
                            break;

                        case GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources:
                            await _googleSyncService.RemoveUserFromTeamResourcesAsync(
                                outboxEvent.TeamId,
                                outboxEvent.UserId,
                                cancellationToken);
                            break;

                        default:
                            throw new InvalidOperationException($"Unknown outbox event type '{outboxEvent.EventType}'.");
                    }

                    await _outboxRepository.MarkProcessedAsync(
                        outboxEvent.Id, _clock.GetCurrentInstant(), cancellationToken);
                    _syncOperationsCounter.Add(1, new KeyValuePair<string, object?>("result", "success"));

                    // Only mark user as Valid when the event actually touched Google APIs
                    // (AddUserToTeamResources with linked resources). RemoveUserFromTeamResources
                    // is a no-op, and Add with zero resources doesn't validate the email.
                    if (string.Equals(outboxEvent.EventType, GoogleSyncOutboxEventTypes.AddUserToTeamResources, StringComparison.Ordinal))
                    {
                        var activeResources = await _resourceRepository
                            .GetActiveByTeamIdAsync(outboxEvent.TeamId, cancellationToken);
                        if (activeResources.Count > 0)
                        {
                            await _userService.TrySetGoogleEmailStatusFromSyncAsync(
                                outboxEvent.UserId, GoogleEmailStatus.Valid, cancellationToken);
                        }
                    }
                }
                catch (Google.GoogleApiException ex) when (IsPermanentError(ex))
                {
                    _syncOperationsCounter.Add(1, new KeyValuePair<string, object?>("result", "permanent_failure"));

                    await _outboxRepository.MarkPermanentlyFailedAsync(
                        outboxEvent.Id, _clock.GetCurrentInstant(), ex.Message, cancellationToken);

                    _logger.LogWarning(
                        ex,
                        "Permanent failure processing Google sync outbox event {OutboxId} ({EventType}) for user {UserEmail} in team {TeamName} — HTTP {StatusCode}, not retrying",
                        outboxEvent.Id,
                        outboxEvent.EventType,
                        userEmailLookup.GetValueOrDefault(outboxEvent.UserId, "unknown"),
                        teamNameLookup.GetValueOrDefault(outboxEvent.TeamId, outboxEvent.TeamId.ToString()),
                        ex.Error?.Code);

                    // Mark user's Google email as Rejected
                    await _userService.TrySetGoogleEmailStatusFromSyncAsync(
                        outboxEvent.UserId, GoogleEmailStatus.Rejected, cancellationToken);
                }
                catch (Exception ex)
                {
                    _syncOperationsCounter.Add(1, new KeyValuePair<string, object?>("result", "failure"));

                    var (exhausted, retryCount) = await _outboxRepository.IncrementRetryAsync(
                        outboxEvent.Id,
                        _clock.GetCurrentInstant(),
                        ex.Message,
                        MaxRetryCount,
                        cancellationToken);

                    _logger.LogError(
                        ex,
                        "Failed processing Google sync outbox event {OutboxId} ({EventType}) for user {UserEmail} in team {TeamName} — attempt {Attempt}/{MaxRetries}",
                        outboxEvent.Id,
                        outboxEvent.EventType,
                        userEmailLookup.GetValueOrDefault(outboxEvent.UserId, "unknown"),
                        teamNameLookup.GetValueOrDefault(outboxEvent.TeamId, outboxEvent.TeamId.ToString()),
                        retryCount,
                        MaxRetryCount);

                    // Notify admins on final failure (exhausted retries)
                    if (exhausted)
                    {
                        var snippet = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                        try
                        {
                            await _notificationService.SendToRoleAsync(
                                NotificationSource.SyncError,
                                NotificationClass.Actionable,
                                NotificationPriority.High,
                                "Google sync event failed after all retries",
                                RoleNames.Admin,
                                body: $"Event {outboxEvent.EventType} for team {outboxEvent.TeamId} failed: {snippet}",
                                actionUrl: "/Google/SyncOutbox",
                                actionLabel: "View →",
                                cancellationToken: cancellationToken);
                        }
                        catch (Exception notifEx)
                        {
                            _logger.LogError(notifEx,
                                "Failed to dispatch SyncError notification for outbox event {OutboxId}",
                                outboxEvent.Id);
                        }
                    }
                }
            }

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "process_google_sync_outbox"),
                new KeyValuePair<string, object?>("result", "success"));
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "process_google_sync_outbox"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error processing Google sync outbox");
            throw;
        }
    }

    private static bool IsPermanentError(Google.GoogleApiException ex)
    {
        return ex.Error?.Code is int code && PermanentErrorCodes.Contains(code);
    }
}
