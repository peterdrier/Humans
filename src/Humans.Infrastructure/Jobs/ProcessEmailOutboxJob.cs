using System.Text.Json;
using Hangfire;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Metering;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
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
/// Email section's <c>email_outbox_messages</c> + <c>IsEmailSendingPaused</c>
/// state is owned by a single repository per §15. Campaign grant status updates
/// route through <see cref="ICampaignService"/> so the Campaigns section owns
/// <c>campaign_grants</c> (design-rules §2c) — this job no longer touches
/// <c>HumansDbContext</c> at all.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class ProcessEmailOutboxJob(
    IEmailOutboxRepository outboxRepo,
    ICampaignService campaignService,
    IEmailTransport transport,
    IHumansMetrics metrics,
    IMeters meters,
    IClock clock,
    IOptions<EmailSettings> settings,
    ILogger<ProcessEmailOutboxJob> logger) : IRecurringJob
{
    private readonly IMeter _outboxPendingMeter = meters.Declare(
        "humans.email_outbox_pending",
        new MeterMetadata("Emails pending in the outbox queue", "{emails}"));

    private readonly EmailSettings _settings = settings.Value;

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check global pause flag
        if (await outboxRepo.GetSendingPausedAsync(cancellationToken))
        {
            logger.LogInformation("Email sending is paused, skipping outbox processing");
            return;
        }

        var now = clock.GetCurrentInstant();
        var staleThreshold = now - Duration.FromMinutes(5);

        // 2. Select batch of messages to process
        var messages = await outboxRepo.GetProcessingBatchAsync(
            now, staleThreshold, _settings.OutboxMaxRetries, _settings.OutboxBatchSize, cancellationToken);

        if (messages.Count == 0)
        {
            return;
        }

        // 3. Mark batch as picked up (prevents concurrent processor runs picking the same rows)
        await outboxRepo.MarkPickedUpAsync(
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
                    await outboxRepo.MarkSentAsync(message.Id, now, cancellationToken);
                    logger.LogInformation(
                        "Skipped email {MessageId} to test address {Email}",
                        message.Id, message.RecipientEmail);
                    continue;
                }

                Dictionary<string, string>? extraHeaders = null;
                if (!string.IsNullOrEmpty(message.ExtraHeaders))
                {
                    extraHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(message.ExtraHeaders);
                }

                await transport.SendAsync(
                    message.RecipientEmail,
                    message.RecipientName,
                    message.Subject,
                    message.HtmlBody,
                    message.PlainTextBody,
                    message.ReplyTo,
                    extraHeaders,
                    cancellationToken);

                // Success — mark as sent BEFORE throttle delay to avoid re-send on cancellation
                await outboxRepo.MarkSentAsync(message.Id, now, cancellationToken);
                metrics.RecordEmailSent(message.TemplateName);

                // Update campaign grant status if applicable — routed via
                // ICampaignService so the Campaigns section owns campaign_grants.
                if (message.CampaignGrantId.HasValue)
                {
                    await campaignService.UpdateGrantEmailStatusAsync(
                        message.CampaignGrantId.Value,
                        EmailOutboxStatus.Sent,
                        now,
                        cancellationToken);
                }

                // Throttle: 1 second delay between sends to avoid SMTP rate limits
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                // Failure
                var nextRetryAt = now + Duration.FromMinutes((long)Math.Pow(2, message.RetryCount + 1));
                await outboxRepo.MarkFailedAsync(message.Id, now, ex.Message, nextRetryAt, cancellationToken);
                metrics.RecordEmailFailed(message.TemplateName);

                // Update campaign grant status if applicable — routed via ICampaignService.
                if (message.CampaignGrantId.HasValue)
                {
                    await campaignService.UpdateGrantEmailStatusAsync(
                        message.CampaignGrantId.Value,
                        EmailOutboxStatus.Failed,
                        now,
                        cancellationToken);
                }

                logger.LogError(
                    ex,
                    "Failed sending email outbox message {MessageId} ({TemplateName}) attempt {Attempt}",
                    message.Id,
                    message.TemplateName,
                    message.RetryCount + 1);
            }
        }

        // 5. Set outbox_pending gauge
        var pendingCount = await outboxRepo.GetPendingCountAsync(_settings.OutboxMaxRetries, cancellationToken);
        _outboxPendingMeter.Set(pendingCount);

        // 6. Record successful job run
        metrics.RecordJobRun("process_email_outbox", "success");
    }
}
