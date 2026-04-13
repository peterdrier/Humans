using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Purges old sent messages from the email outbox. Runs weekly.
/// </summary>
public class CleanupEmailOutboxJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly EmailSettings _settings;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<CleanupEmailOutboxJob> _logger;

    public CleanupEmailOutboxJob(
        HumansDbContext dbContext,
        IClock clock,
        IOptions<EmailSettings> settings,
        IHumansMetrics metrics,
        ILogger<CleanupEmailOutboxJob> logger)
    {
        _dbContext = dbContext;
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

            var toDelete = await _dbContext.EmailOutboxMessages
                .Where(m => m.Status == EmailOutboxStatus.Sent && m.SentAt < cutoff)
                .ToListAsync(cancellationToken);

            if (toDelete.Count > 0)
            {
                _dbContext.EmailOutboxMessages.RemoveRange(toDelete);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "CleanupEmailOutboxJob deleted {Count} sent messages older than {Cutoff}",
                toDelete.Count,
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
