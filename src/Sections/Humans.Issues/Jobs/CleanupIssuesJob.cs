using Hangfire;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;

namespace Humans.Issues.Jobs;

/// <summary>
/// Hangfire rewrites the stored type string on every startup, so the job id, not the type, is the stable key.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupIssuesJob(IIssuesRetention issues, IHumansMetrics metrics, ILogger<CleanupIssuesJob> logger)
    : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var deleted = await issues.PurgeExpiredAsync(cancellationToken);

            logger.LogInformation(
                "CleanupIssuesJob: deleted {Count} expired issues",
                deleted);

            metrics.RecordJobRun("cleanup_issues", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("cleanup_issues", "failure");
            logger.LogError(ex, "Error cleaning up expired issues");
            throw;
        }
    }
}
