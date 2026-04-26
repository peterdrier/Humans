using System.Diagnostics.Metrics;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Metering;
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
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<CleanupEmailOutboxJob> _logger;

    public CleanupEmailOutboxJob(
        IEmailOutboxRepository outboxRepo,
        IClock clock,
        IOptions<EmailSettings> settings,
        IMeters meters,
        ILogger<CleanupEmailOutboxJob> logger)
    {
        _outboxRepo = outboxRepo;
        _clock = clock;
        _settings = settings.Value;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
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

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "cleanup_email_outbox"),
                new KeyValuePair<string, object?>("result", "success"));
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "cleanup_email_outbox"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error cleaning up email outbox");
            throw;
        }
    }
}
