using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Issues;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges issues that entered a terminal state (Resolved / WontFix / Duplicate)
/// at least 6 months ago, plus their screenshot directories. Runs daily.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupIssuesJob(IIssuesService issues, IHumansMetrics metrics, ILogger<CleanupIssuesJob> logger)
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
