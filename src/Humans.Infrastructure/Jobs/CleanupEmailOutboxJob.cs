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
public class CleanupEmailOutboxJob : IRecurringJob
{
    private readonly IEmailOutboxRepository _outboxRepo;
    private readonly IClock _clock;
    private readonly EmailSettings _settings;
    private readonly IJobRunMetrics _metrics;
    private readonly ILogger<CleanupEmailOutboxJob> _logger;

    public CleanupEmailOutboxJob(
        IEmailOutboxRepository outboxRepo,
        IClock clock,
        IOptions<EmailSettings> settings,
        IJobRunMetrics metrics,
        ILogger<CleanupEmailOutboxJob> logger)
    {
        _outboxRepo = outboxRepo;
        _clock = clock;
        _settings = settings.Value;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoff = _clock.GetCurrentInstant() - Duration.FromDays(_settings.OutboxRetentionDays);

            var deletedCount = await _outboxRepo.DeleteSentOlderThanAsync(cutoff, cancellationToken);

            _logger.LogInformation(
                "CleanupEmailOutboxJob deleted {Count} sent messages older than {Cutoff}",
                deletedCount,
                cutoff);

            _metrics.RecordJobRun("cleanup_email_outbox", "success");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("cleanup_email_outbox", "failure");
            _logger.LogError(ex, "Error cleaning up email outbox");
            throw;
        }
    }
}
