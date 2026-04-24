using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
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
public class ProcessEmailOutboxJob : IRecurringJob
{
    private readonly IEmailOutboxRepository _outboxRepo;
    private readonly ICampaignService _campaignService;
    private readonly IEmailTransport _transport;
    private readonly IEmailMetrics _emailMetrics;
    private readonly IJobRunMetrics _jobMetrics;
    private readonly IClock _clock;
    private readonly EmailSettings _settings;
    private readonly ILogger<ProcessEmailOutboxJob> _logger;

    public ProcessEmailOutboxJob(
        IEmailOutboxRepository outboxRepo,
        ICampaignService campaignService,
        IEmailTransport transport,
        IEmailMetrics emailMetrics,
        IJobRunMetrics jobMetrics,
        IClock clock,
        IOptions<EmailSettings> settings,
        ILogger<ProcessEmailOutboxJob> logger)
    {
        _outboxRepo = outboxRepo;
        _campaignService = campaignService;
        _transport = transport;
        _emailMetrics = emailMetrics;
        _jobMetrics = jobMetrics;
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
                _emailMetrics.RecordSent(message.TemplateName);

                // Update campaign grant status if applicable — routed via
                // ICampaignService so the Campaigns section owns campaign_grants.
                if (message.CampaignGrantId.HasValue)
                {
                    await _campaignService.UpdateGrantEmailStatusAsync(
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
                await _outboxRepo.MarkFailedAsync(message.Id, now, ex.Message, nextRetryAt, cancellationToken);
                _emailMetrics.RecordFailed(message.TemplateName);

                // Update campaign grant status if applicable — routed via ICampaignService.
                if (message.CampaignGrantId.HasValue)
                {
                    await _campaignService.UpdateGrantEmailStatusAsync(
                        message.CampaignGrantId.Value,
                        EmailOutboxStatus.Failed,
                        now,
                        cancellationToken);
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
        _emailMetrics.SetOutboxPending(pendingCount);

        // 6. Record successful job run
        _jobMetrics.RecordJobRun("process_email_outbox", "success");
    }
}
