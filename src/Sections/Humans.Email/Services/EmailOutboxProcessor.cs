using Humans.Base.Attributes;
using System.Text.Json;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Metering;
using Humans.Base.Metering;
using Humans.Campaigns.Contracts;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Email.Data;
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
/// The job is the scheduler shim; the queue semantics are here (design §15 step 6b).
/// </summary>
/// <remarks>
/// The grant mirror goes through <see cref="ICampaignService"/> so Campaigns owns
/// <c>campaign_grants</c> (design-rules §2c); the pause flag routes to SystemSettings
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

        var pendingCount = await outboxRepo.GetPendingCountAsync(_settings.OutboxMaxRetries, cancellationToken);
        _outboxPendingMeter.Set(pendingCount);
    }
}
