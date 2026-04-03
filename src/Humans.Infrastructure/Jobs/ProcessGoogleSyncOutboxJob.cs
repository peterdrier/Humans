using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Drains queued Google sync outbox events and executes the underlying sync operations.
/// SyncSettings enforcement is handled by the gateway methods in GoogleWorkspaceSyncService.
/// </summary>
public class ProcessGoogleSyncOutboxJob : IRecurringJob
{
    private const int BatchSize = 100;
    private const int MaxRetryCount = 10;

    /// <summary>
    /// HTTP status codes that indicate a permanent user-level failure (do not retry).
    /// 400 = bad request (invalid email format), 404 = user not found.
    /// Note: 403 is excluded because it typically indicates a resource-level permission issue
    /// (service account lacks access), not a user email problem.
    /// </summary>
    private static readonly HashSet<int> PermanentErrorCodes = [400, 404];

    private readonly HumansDbContext _dbContext;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly INotificationService _notificationService;
    private readonly HumansMetricsService _metrics;
    private readonly IClock _clock;
    private readonly ILogger<ProcessGoogleSyncOutboxJob> _logger;

    public ProcessGoogleSyncOutboxJob(
        HumansDbContext dbContext,
        IGoogleSyncService googleSyncService,
        INotificationService notificationService,
        HumansMetricsService metrics,
        IClock clock,
        ILogger<ProcessGoogleSyncOutboxJob> logger)
    {
        _dbContext = dbContext;
        _googleSyncService = googleSyncService;
        _notificationService = notificationService;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var pendingEvents = await _dbContext.GoogleSyncOutboxEvents
            .Where(e => e.ProcessedAt == null && !e.FailedPermanently && e.RetryCount < MaxRetryCount)
            .OrderBy(e => e.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pendingEvents.Count == 0)
        {
            return;
        }

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

                outboxEvent.ProcessedAt = _clock.GetCurrentInstant();
                outboxEvent.LastError = null;
                _metrics.RecordSyncOperation("success");

                // Only mark user as Valid when the event actually touched Google APIs
                // (AddUserToTeamResources with linked resources). RemoveUserFromTeamResources
                // is a no-op, and Add with zero resources doesn't validate the email.
                if (string.Equals(outboxEvent.EventType, GoogleSyncOutboxEventTypes.AddUserToTeamResources, StringComparison.Ordinal))
                {
                    var hasResources = await _dbContext.GoogleResources
                        .AnyAsync(r => r.TeamId == outboxEvent.TeamId && r.IsActive, cancellationToken);
                    if (hasResources)
                    {
                        await MarkUserGoogleEmailStatusAsync(outboxEvent.UserId, GoogleEmailStatus.Valid, cancellationToken);
                    }
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Google.GoogleApiException ex) when (IsPermanentError(ex))
            {
                _metrics.RecordSyncOperation("permanent_failure");
                outboxEvent.FailedPermanently = true;
                outboxEvent.ProcessedAt = _clock.GetCurrentInstant();
                outboxEvent.LastError = ex.Message.Length > 4000
                    ? ex.Message[..4000]
                    : ex.Message;

                _logger.LogWarning(
                    ex,
                    "Permanent failure processing Google sync outbox event {OutboxId} ({EventType}) — HTTP {StatusCode}, not retrying",
                    outboxEvent.Id,
                    outboxEvent.EventType,
                    ex.Error?.Code);

                // Mark user's Google email as Rejected
                await MarkUserGoogleEmailStatusAsync(outboxEvent.UserId, GoogleEmailStatus.Rejected, cancellationToken);

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _metrics.RecordSyncOperation("failure");
                outboxEvent.RetryCount += 1;
                outboxEvent.LastError = ex.Message.Length > 4000
                    ? ex.Message[..4000]
                    : ex.Message;

                _logger.LogError(
                    ex,
                    "Failed processing Google sync outbox event {OutboxId} ({EventType}) attempt {Attempt}",
                    outboxEvent.Id,
                    outboxEvent.EventType,
                    outboxEvent.RetryCount);

                await _dbContext.SaveChangesAsync(cancellationToken);

                // Notify admins on final failure (exhausted retries)
                if (outboxEvent.RetryCount >= MaxRetryCount)
                {
                    try
                    {
                        await _notificationService.SendToRoleAsync(
                            NotificationSource.SyncError,
                            NotificationClass.Actionable,
                            NotificationPriority.High,
                            "Google sync event failed after all retries",
                            RoleNames.Admin,
                            body: $"Event {outboxEvent.EventType} for team {outboxEvent.TeamId} failed: {outboxEvent.LastError?[..Math.Min(200, outboxEvent.LastError.Length)]}",
                            actionUrl: "/Google/Sync",
                            actionLabel: "View \u2192",
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
        _metrics.RecordJobRun("process_google_sync_outbox", "success");
    }

    private static bool IsPermanentError(Google.GoogleApiException ex)
    {
        return ex.Error?.Code is int code && PermanentErrorCodes.Contains(code);
    }

    private async Task MarkUserGoogleEmailStatusAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FindAsync([userId], cancellationToken);
        if (user is not null)
        {
            user.GoogleEmailStatus = status;
        }
    }
}
