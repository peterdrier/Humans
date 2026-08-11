using Hangfire;
using Humans.Application.Interfaces;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges old notifications. Runs daily. The retention rule itself — resolved older than
/// 7 days, unresolved informational older than 30 days, unresolved rows of retired
/// sources — lives inside the Notifications section behind
/// <see cref="INotificationRetention"/>; this job is the scheduler shim around it.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupNotificationsJob(
    INotificationRetention notifications,
    IHumansMetrics metrics,
    ILogger<CleanupNotificationsJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (resolvedDeleted, staleDeleted, retiredDeleted) =
                await notifications.PurgeExpiredAsync(cancellationToken);

            logger.LogInformation(
                "CleanupNotificationsJob: deleted {ResolvedCount} resolved, {StaleCount} stale informational, and {RetiredCount} retired-source notifications",
                resolvedDeleted, staleDeleted, retiredDeleted);

            metrics.RecordJobRun("cleanup_notifications", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("cleanup_notifications", "failure");
            logger.LogError(ex, "Error cleaning up notifications");
            throw;
        }
    }
}
