using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Processes queued email outbox messages by sending them via the email transport.
/// Runs every 1 minute via Hangfire.
/// </summary>
/// <remarks>
/// The outbox reads/writes go through <see cref="IEmailOutboxRepository"/> so the
/// Email section's <c>email_outbox_messages</c> + <c>IsEmailSendingPaused</c> state
/// is owned by a single repository per §15. The campaign-grant status update is
/// a cross-section write (Campaigns section's <c>campaign_grants</c> table) and
/// continues to use <see cref="HumansDbContext"/> directly — that cross-section
/// write will move behind an <c>ICampaignService</c> surface when Campaigns
/// completes its §15 migration.
/// </remarks>
public class ProcessEmailOutboxJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailOutboxRepository _outboxRepo;
    private readonly IEmailTransport _transport;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly EmailSettings _settings;
    private readonly ILogger<ProcessEmailOutboxJob> _logger;

    public ProcessEmailOutboxJob(
        HumansDbContext dbContext,
        IEmailOutboxRepository outboxRepo,
        IEmailTransport transport,
        IHumansMetrics metrics,
        IClock clock,
        IOptions<EmailSettings> settings,
        ILogger<ProcessEmailOutboxJob> logger)
    {
        _dbContext = dbContext;
        _outboxRepo = outboxRepo;
        _transport = transport;
        _metrics = metrics;
        _clock = clock;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check global pause flag
        if (await _outboxRepo.GetSendingPausedAsync(cancellationToken))
        {
            _logger.LogInformation("Email sending is paused, skipping outbox processing");
            return;
        }

        var now = _clock.GetCurrentInstant();
        var staleThreshold = now - Duration.FromMinutes(5);

        // 2. Select batch of messages to process
        var messages = await _outboxRepo.GetProcessingBatchAsync(
            now, staleThreshold, _settings.OutboxMaxRetries, _settings.OutboxBatchSize, cancellationToken);

        if (messages.Count == 0)
        {
            return;
        }

        // 3. Mark batch as picked up (prevents concurrent processor runs picking the same rows)
        await _outboxRepo.MarkPickedUpAsync(
            messages.Select(m => m.Id).ToList(), now, cancellationToken);

        // 4. Process each message
        foreach (var message in messages)
        {
            try
            {
                // Skip invalid test addresses — sending to these bounces and damages sender reputation
                if (message.RecipientEmail.EndsWith("@localhost", StringComparison.OrdinalIgnoreCase) ||
                    message.RecipientEmail.EndsWith("@ticketstub.local", StringComparison.OrdinalIgnoreCase))
                {
                    await _outboxRepo.MarkSentAsync(message.Id, now, cancellationToken);
                    _logger.LogInformation(
                        "Skipped email {MessageId} to test address {Email}",
                        message.Id, message.RecipientEmail);
                    continue;
                }

                Dictionary<string, string>? extraHeaders = null;
                if (!string.IsNullOrEmpty(message.ExtraHeaders))
                {
                    extraHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(message.ExtraHeaders);
                }

                await _transport.SendAsync(
                    message.RecipientEmail,
                    message.RecipientName,
                    message.Subject,
                    message.HtmlBody,
                    message.PlainTextBody,
                    message.ReplyTo,
                    extraHeaders,
                    cancellationToken);

                // Success — mark as sent BEFORE throttle delay to avoid re-send on cancellation
                await _outboxRepo.MarkSentAsync(message.Id, now, cancellationToken);
                _metrics.RecordEmailSent(message.TemplateName);

                // Update campaign grant status if applicable.
                // Cross-section write into the Campaigns section; still direct on the
                // DbContext until Campaigns §15 lands. Intentional and tracked.
                if (message.CampaignGrantId.HasValue)
                {
                    var grant = await _dbContext.CampaignGrants.FindAsync(
                        new object[] { message.CampaignGrantId.Value }, cancellationToken);
                    if (grant is not null)
                    {
                        grant.LatestEmailStatus = EmailOutboxStatus.Sent;
                        grant.LatestEmailAt = now;
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }
                }

                // Throttle: 1 second delay between sends to avoid SMTP rate limits
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                // Failure
                var nextRetryAt = now + Duration.FromMinutes((long)Math.Pow(2, message.RetryCount + 1));
                await _outboxRepo.MarkFailedAsync(message.Id, now, ex.Message, nextRetryAt, cancellationToken);
                _metrics.RecordEmailFailed(message.TemplateName);

                // Update campaign grant status if applicable (cross-section write — see above)
                if (message.CampaignGrantId.HasValue)
                {
                    var grant = await _dbContext.CampaignGrants.FindAsync(
                        new object[] { message.CampaignGrantId.Value }, cancellationToken);
                    if (grant is not null)
                    {
                        grant.LatestEmailStatus = EmailOutboxStatus.Failed;
                        grant.LatestEmailAt = now;
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }
                }

                _logger.LogError(
                    ex,
                    "Failed sending email outbox message {MessageId} ({TemplateName}) attempt {Attempt}",
                    message.Id,
                    message.TemplateName,
                    message.RetryCount + 1);
            }
        }

        // 5. Set outbox_pending gauge
        var pendingCount = await _outboxRepo.GetPendingCountAsync(_settings.OutboxMaxRetries, cancellationToken);
        _metrics.SetEmailOutboxPending(pendingCount);

        // 6. Record successful job run
        _metrics.RecordJobRun("process_email_outbox", "success");
    }
}
