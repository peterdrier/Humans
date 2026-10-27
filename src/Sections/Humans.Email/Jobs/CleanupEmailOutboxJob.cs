using Hangfire;
using Humans.Base.Interfaces;
using Humans.Email.Services;

namespace Humans.Email.Jobs;

[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class CleanupEmailOutboxJob(
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
