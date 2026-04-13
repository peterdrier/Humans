using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Processes queued email outbox messages by sending them via the email transport.
/// Runs every 1 minute via Hangfire.
/// </summary>
public class ProcessEmailOutboxJob : IRecurringJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailTransport _transport;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly EmailSettings _settings;
    private readonly ILogger<ProcessEmailOutboxJob> _logger;

    public ProcessEmailOutboxJob(
        HumansDbContext dbContext,
        IEmailTransport transport,
        IHumansMetrics metrics,
        IClock clock,
        IOptions<EmailSettings> settings,
        ILogger<ProcessEmailOutboxJob> logger)
    {
        _dbContext = dbContext;
        _transport = transport;
        _metrics = metrics;
        _clock = clock;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check global pause flag
        var pauseSetting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused, cancellationToken);

        if (pauseSetting is { Value: "true" })
        {
            _logger.LogInformation("Email sending is paused, skipping outbox processing");
            return;
        }

        var now = _clock.GetCurrentInstant();
        var staleThreshold = now - Duration.FromMinutes(5);

        // 2. Select batch of messages to process
        var messages = await _dbContext.EmailOutboxMessages
            .Where(m => m.SentAt == null
                && m.RetryCount < _settings.OutboxMaxRetries
                && (m.NextRetryAt == null || m.NextRetryAt <= now)
                && (m.PickedUpAt == null || m.PickedUpAt < staleThreshold))
            .OrderBy(m => m.CreatedAt)
            .Take(_settings.OutboxBatchSize)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return;
        }

        // 3. Mark batch as picked up
        foreach (var message in messages)
        {
            message.PickedUpAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // 4. Process each message
        foreach (var message in messages)
        {
            try
            {
                // Skip invalid test addresses — sending to these bounces and damages sender reputation
                if (message.RecipientEmail.EndsWith("@localhost", StringComparison.OrdinalIgnoreCase) ||
                    message.RecipientEmail.EndsWith("@ticketstub.local", StringComparison.OrdinalIgnoreCase))
                {
                    message.Status = EmailOutboxStatus.Sent;
                    message.SentAt = now;
                    message.PickedUpAt = null;
                    _logger.LogInformation(
                        "Skipped email {MessageId} to test address {Email}",
                        message.Id, message.RecipientEmail);
                    await _dbContext.SaveChangesAsync(cancellationToken);
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
                message.Status = EmailOutboxStatus.Sent;
                message.SentAt = now;
                message.PickedUpAt = null;
                _metrics.RecordEmailSent(message.TemplateName);

                // Update campaign grant status if applicable
                if (message.CampaignGrantId.HasValue)
                {
                    var grant = await _dbContext.CampaignGrants.FindAsync(
                        new object[] { message.CampaignGrantId.Value }, cancellationToken);
                    if (grant is not null)
                    {
                        grant.LatestEmailStatus = EmailOutboxStatus.Sent;
                        grant.LatestEmailAt = now;
                    }
                }

                await _dbContext.SaveChangesAsync(cancellationToken);

                // Throttle: 1 second delay between sends to avoid SMTP rate limits
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                // Failure
                message.Status = EmailOutboxStatus.Failed;
                message.RetryCount += 1;
                message.LastError = ex.Message.Length > 4000
                    ? ex.Message[..4000]
                    : ex.Message;
                message.NextRetryAt = now + Duration.FromMinutes((long)Math.Pow(2, message.RetryCount));
                message.PickedUpAt = null;
                _metrics.RecordEmailFailed(message.TemplateName);

                // Update campaign grant status if applicable
                if (message.CampaignGrantId.HasValue)
                {
                    var grant = await _dbContext.CampaignGrants.FindAsync(
                        new object[] { message.CampaignGrantId.Value }, cancellationToken);
                    if (grant is not null)
                    {
                        grant.LatestEmailStatus = EmailOutboxStatus.Failed;
                        grant.LatestEmailAt = now;
                    }
                }

                _logger.LogError(
                    ex,
                    "Failed sending email outbox message {MessageId} ({TemplateName}) attempt {Attempt}",
                    message.Id,
                    message.TemplateName,
                    message.RetryCount);

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // 5. Set outbox_pending gauge
        var pendingCount = await _dbContext.EmailOutboxMessages
            .CountAsync(m => m.SentAt == null && m.RetryCount < _settings.OutboxMaxRetries, cancellationToken);
        _metrics.SetEmailOutboxPending(pendingCount);

        // 6. Record successful job run
        _metrics.RecordJobRun("process_email_outbox", "success");
    }
}
