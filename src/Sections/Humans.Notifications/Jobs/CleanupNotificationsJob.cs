using Hangfire;
using Humans.Application.Interfaces;
using Humans.Notifications.Contracts;

namespace Humans.Notifications.Jobs;

/// <summary>
/// Purges old notifications. Runs daily. The retention rule itself — resolved older than
/// 7 days, unresolved informational older than 30 days, unresolved rows of retired
/// sources — lives inside the Notifications section behind
/// <see cref="INotificationRetention"/>; this job is the scheduler shim around it.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-1
/// (nobodies-collective/Humans#866). The "Hangfire pins a job to its assembly" claim that
/// kept it in Base was re-measured and is false: <c>RecurringJob.AddOrUpdate&lt;T&gt;(id, …)</c>
/// rewrites the stored type string on every startup, so the job id is the stable key. It
/// sits under <c>Jobs/</c> because Shell names the concrete type at registration and
/// HUM0034 makes every other public type in a section assembly an error.
/// </remarks>
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
