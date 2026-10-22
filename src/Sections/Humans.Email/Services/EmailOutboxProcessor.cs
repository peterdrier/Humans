using System.Text.Json;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Metering;
using Humans.Base.Metering;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Base.Configuration;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Email.Services;

internal sealed class EmailOutboxProcessor(
    IEmailOutboxRepository outboxRepo,
    IEmailOutboxManagement emailOutboxService,
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

    internal Func<TimeSpan, CancellationToken, Task> ThrottleDelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

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
            // Skip invalid test addresses — sending to these bounces and damages sender reputation
            if (EmailTestAddress.IsTestAddress(message.RecipientEmail))
            {
                await outboxRepo.MarkSentAsync(message.Id, now, cancellationToken);
                logger.LogInformation(
                    "Skipped email {MessageId} to test address {Email}",
                    message.Id, message.RecipientEmail);
                continue;
            }

            try
            {
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
            }
            catch (Exception ex)
            {
                var failedAt = clock.GetCurrentInstant();
                var nextRetryAt = failedAt + Duration.FromMinutes((long)Math.Pow(2, message.RetryCount + 1));
                await outboxRepo.MarkFailedAsync(message.Id, failedAt, ex.Message, nextRetryAt, cancellationToken);
                metrics.RecordEmailFailed(message.TemplateName);
                await TryIncrementDailySendCountAsync(message, failedAt, succeeded: false, cancellationToken);

                logger.LogError(
                    ex,
                    "Failed sending email outbox message {MessageId} ({TemplateName}) attempt {Attempt}",
                    message.Id,
                    message.TemplateName,
                    message.RetryCount + 1);
                continue;
            }

            // Transport succeeded. A persistence or throttle failure must not
            // reclassify accepted mail as a failed delivery attempt.
            var sentAt = clock.GetCurrentInstant();
            await outboxRepo.MarkSentAsync(message.Id, sentAt, cancellationToken);
            metrics.RecordEmailSent(message.TemplateName);
            await TryIncrementDailySendCountAsync(message, sentAt, succeeded: true, cancellationToken);

            // Stays under the SMTP relay's rate limit.
            await ThrottleDelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var pendingCount = await outboxRepo.GetPendingCountAsync(_settings.OutboxMaxRetries, cancellationToken);
        _outboxPendingMeter.Set(pendingCount);
    }

    /// <summary>Analytics, not delivery state: a failed write is logged and changes nothing else.</summary>
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
