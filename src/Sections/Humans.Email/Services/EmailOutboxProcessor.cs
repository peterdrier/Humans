using Humans.Base.Attributes;
using System.Text.Json;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Metering;
using Humans.Base.Metering;
using Humans.Campaigns.Contracts;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Email.Services;

/// <summary>
/// Drains the outbox: pause check, batch pick-up, per-message transport call, retry
/// backoff, and the campaign-grant status mirror. Lifted out of
/// <c>ProcessEmailOutboxJob</c> at the section's G5 move — the job kept
/// <see cref="IEmailOutboxRepository"/> and <see cref="IEmailTransport"/> in Base, which
/// is a job reaching past the service layer, and neither type is nameable from Base now.
/// The job is the scheduler shim; the queue semantics are here (the G5 playbook, step 6b).
/// </summary>
/// <remarks>
/// The grant mirror goes through <see cref="ICampaignService"/> so Campaigns owns
/// <c>campaign_grants</c> (design-rules §2c); the pause flag routes to Settings
/// through <see cref="IEmailOutboxService.IsEmailPausedAsync"/>.
/// </remarks>
[CrossSectionWrite("Marks the campaign grant email status after send.")]
internal sealed class EmailOutboxProcessor(
    IEmailOutboxRepository outboxRepo,
    IEmailOutboxService emailOutboxService,
    ICampaignService campaignService,
    IEmailTransport transport,
    IHumansMetrics metrics,
    IMeters meters,
    IClock clock,
    IOptions<EmailSettings> settings,
    ILogger<EmailOutboxProcessor> logger) : IEmailOutboxProcessor, IApplicationService
{
    private readonly IMeter _outboxPendingMeter = meters.Declare(
        "humans.email_outbox_pending",
        new MeterMetadata("Emails pending in the outbox queue", "{emails}"));

    private readonly EmailSettings _settings = settings.Value;

    public async Task ProcessQueuedAsync(CancellationToken cancellationToken = default)
    {
        if (await emailOutboxService.IsEmailPausedAsync(cancellationToken))
        {
            logger.LogInformation("Email sending is paused, skipping outbox processing");
            return;
        }

        var now = clock.GetCurrentInstant();
        var staleThreshold = now - Duration.FromMinutes(5);

        var messages = await outboxRepo.GetProcessingBatchAsync(
            now, staleThreshold, _settings.OutboxMaxRetries, _settings.OutboxBatchSize, cancellationToken);

        if (messages.Count == 0)
        {
            return;
        }

        await outboxRepo.MarkPickedUpAsync(
            messages.Select(m => m.Id).ToList(), now, cancellationToken);

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
                var sentAt = clock.GetCurrentInstant();
                await outboxRepo.MarkSentAsync(message.Id, sentAt, cancellationToken);
                metrics.RecordEmailSent(message.TemplateName);
                await TryIncrementDailySendCountAsync(message, sentAt, succeeded: true, cancellationToken);

                // Update campaign grant status if applicable — routed via
                // ICampaignService so the Campaigns section owns campaign_grants.
                // Bookkeeping only: a failure here must never fall into the catch
                // below and re-tally an already-sent message as failed.
                if (message.CampaignGrantId.HasValue)
                {
                    await TryUpdateGrantEmailStatusAsync(
                        message.CampaignGrantId.Value, EmailOutboxStatus.Sent, sentAt, message.Id, cancellationToken);
                }

                // Throttle: 1 second delay between sends to avoid SMTP rate limits
                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                var failedAt = clock.GetCurrentInstant();
                var nextRetryAt = failedAt + Duration.FromMinutes((long)Math.Pow(2, message.RetryCount + 1));
                await outboxRepo.MarkFailedAsync(message.Id, failedAt, ex.Message, nextRetryAt, cancellationToken);
                metrics.RecordEmailFailed(message.TemplateName);
                await TryIncrementDailySendCountAsync(message, failedAt, succeeded: false, cancellationToken);

                // Update campaign grant status if applicable — routed via ICampaignService.
                if (message.CampaignGrantId.HasValue)
                {
                    await campaignService.UpdateGrantEmailStatusAsync(
                        message.CampaignGrantId.Value,
                        EmailOutboxStatus.Failed,
                        failedAt,
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

        var pendingCount = await outboxRepo.GetPendingCountAsync(_settings.OutboxMaxRetries, cancellationToken);
        _outboxPendingMeter.Set(pendingCount);
    }

    /// <summary>
    /// The campaign grant mirror is bookkeeping, not delivery state: a write
    /// failure here must never surface as a delivery failure. Left uncaught, it
    /// would fall into the per-message catch above, flip an already-<c>Sent</c>
    /// message to <c>Failed</c> and double-tally both the metric and the daily
    /// send count for the one delivery attempt.
    /// </summary>
    private async Task TryUpdateGrantEmailStatusAsync(
        Guid campaignGrantId, EmailOutboxStatus status, Instant now, Guid messageId, CancellationToken cancellationToken)
    {
        try
        {
            await campaignService.UpdateGrantEmailStatusAsync(campaignGrantId, status, now, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed updating campaign grant {CampaignGrantId} email status to {Status} for message {MessageId}",
                campaignGrantId, status, messageId);
        }
    }

    /// <summary>
    /// The daily tally is analytics, not delivery state: a write failure here must
    /// never surface as a delivery failure. Left uncaught, it would fall into the
    /// per-message catch above, flip an already-<c>Sent</c> message to <c>Failed</c>
    /// and queue it for retry — resending mail solely because the count row failed
    /// to save.
    /// </summary>
    private async Task TryIncrementDailySendCountAsync(
        EmailOutboxMessage message, Instant now, bool succeeded, CancellationToken cancellationToken)
    {
        try
        {
            await outboxRepo.IncrementDailySendCountAsync(
                now.InUtc().Date, message.TemplateName, succeeded, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed incrementing daily send count for {TemplateName} (message {MessageId}, succeeded={Succeeded})",
                message.TemplateName, message.Id, succeeded);
        }
    }
}
