using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges old sent messages from the email outbox. Runs weekly.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class CleanupEmailOutboxJob(
    IEmailOutboxRepository outboxRepo,
    IClock clock,
    IOptions<EmailSettings> settings,
    IHumansMetrics metrics,
    ILogger<CleanupEmailOutboxJob> logger) : IRecurringJob
{
    private readonly EmailSettings _settings = settings.Value;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoff = clock.GetCurrentInstant() - Duration.FromDays(_settings.OutboxRetentionDays);

            var deletedCount = await outboxRepo.DeleteSentOlderThanAsync(cutoff, cancellationToken);

            logger.LogInformation(
                "CleanupEmailOutboxJob deleted {Count} sent messages older than {Cutoff}",
                deletedCount,
                cutoff);

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
