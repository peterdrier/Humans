using Hangfire;
using Humans.Application.Interfaces;
using Humans.Email.Contracts;
using Microsoft.Extensions.Logging;

namespace Humans.Email.Jobs;

/// <summary>
/// Purges old sent messages from the email outbox. Runs weekly. The retention cutoff —
/// <c>Email:OutboxRetentionDays</c> back from now — lives inside the Email section behind
/// <see cref="IEmailOutboxRetention"/>; this job is the scheduler shim around it.
/// </summary>
/// <remarks>
/// Moved out of <c>Humans.Infrastructure/Jobs</c> at G5 lane 5b-1 — see
/// <see cref="ProcessEmailOutboxJob"/> for why the assembly is not load-bearing and why
/// the file sits under <c>Jobs/</c>.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupEmailOutboxJob(
    IEmailOutboxRetention outbox,
    IHumansMetrics metrics,
    ILogger<CleanupEmailOutboxJob> logger) : IRecurringJob
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var deletedCount = await outbox.PurgeExpiredAsync(cancellationToken);

            logger.LogInformation(
                "CleanupEmailOutboxJob deleted {Count} sent messages", deletedCount);

            metrics.RecordJobRun("cleanup_email_outbox", "success");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("cleanup_email_outbox", "failure");
            logger.LogError(ex, "Error cleaning up email outbox");
            throw;
        }
    }
}
