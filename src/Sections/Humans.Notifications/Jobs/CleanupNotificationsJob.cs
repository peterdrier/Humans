using Hangfire;
using Humans.Base.Interfaces;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Jobs;

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
