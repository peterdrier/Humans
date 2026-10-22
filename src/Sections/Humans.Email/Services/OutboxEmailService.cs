using System.Text.Json;
using System.Net;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Services;

internal sealed class OutboxEmailService(
    IEmailOutboxRepository outboxRepo,
    IEmailBodyComposer bodyComposer,
    IImmediateOutboxProcessor immediateProcessor,
    IEmailTransport transport,
    IHumansMetrics metrics,
    IClock clock,
    ICommunicationPreferenceManagement commPrefService,
    ILogger<OutboxEmailService> logger) : IEmailService
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var userId = message.UserId;

        var category = message.Category;

        var optOutEligible = category is not null && !category.Value.IsAlwaysOn();

        if (optOutEligible && userId.HasValue
            && await commPrefService.IsOptedOutAsync(userId.Value, category!.Value, cancellationToken))
        {
            logger.LogInformation(
                "Email suppressed: {TemplateName} to {Recipient} — opted out of {Category}",
                message.TemplateName, message.RecipientEmail, category.Value);
            return;
        }

        string? unsubscribeUrl = null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Feedback-ID"] = BuildFeedbackId(message, category)
        };
        if (optOutEligible && userId.HasValue)
        {
            foreach (var (name, value) in commPrefService.GenerateUnsubscribeHeaders(userId.Value, category!.Value))
            {
                headers[name] = value;
            }
            unsubscribeUrl = commPrefService.GenerateBrowserUnsubscribeUrl(userId.Value, category.Value);
        }
        var extraHeadersJson = JsonSerializer.Serialize(headers);

        var subject = message.Subject;
        var htmlBody = message.HtmlBody;
        if (!message.DoNotPersist)
        {
            var subjectRunes = subject.EnumerateRunes().Take(EmailOutboxMessage.SubjectMaxLength + 1).ToArray();
            if (subjectRunes.Length > EmailOutboxMessage.SubjectMaxLength)
            {
                // Preserve all text when prefixes or substitutions exceed the stored header's bound.
                htmlBody = "<p><strong>" + WebUtility.HtmlEncode(subject) + "</strong></p>\n" + htmlBody;
                subject = string.Concat(subjectRunes.Take(EmailOutboxMessage.SubjectMaxLength - 1)
                    .Select(rune => rune.ToString())) + "…";
            }
        }
        var (wrappedHtml, plainText) = bodyComposer.Compose(htmlBody, unsubscribeUrl, message.Culture);

        if (message.DoNotPersist)
        {
            // Straight to the transport: no row, no retry, no stored copy of a member the erasure
            // just removed. Logged without the address for the same reason.
            await transport.SendAsync(
                message.RecipientEmail, message.RecipientName, message.Subject,
                wrappedHtml, plainText, message.ReplyTo, headers, cancellationToken: cancellationToken,
                redact: true, templateName: message.TemplateName);

            metrics.RecordEmailQueued(message.TemplateName);
            logger.LogInformation(
                "Email sent without an outbox row: {TemplateName}", message.TemplateName);
            return;
        }

        var entity = new EmailOutboxMessage
        {
            Id = message.DeliveryId ?? Guid.NewGuid(),
            RecipientEmail = message.RecipientEmail,
            RecipientName = message.RecipientName,
            Subject = subject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = message.TemplateName,
            UserId = userId,
            CampaignGrantId = message.CampaignGrantId,
            ReplyTo = message.ReplyTo,
            ExtraHeaders = extraHeadersJson,
            Urgent = message.Urgent,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = clock.GetCurrentInstant()
        };

        if (!await outboxRepo.AddAsync(entity, cancellationToken, idempotent: message.DeliveryId.HasValue))
            return;

        metrics.RecordEmailQueued(message.TemplateName);
        logger.LogInformation("Email queued: {TemplateName} to {Recipient}", message.TemplateName, message.RecipientEmail);

        if (message.Urgent)
        {
            try
            {
                immediateProcessor.TriggerImmediate();
                logger.LogInformation("Triggered immediate outbox processing for {TemplateName}", message.TemplateName);
            }
            catch (Exception ex)
            {
                // The message is already durable. The recurring drain will deliver it;
                // reporting a send failure here would invite a duplicate enqueue.
                logger.LogWarning(ex,
                    "Immediate outbox trigger failed for {TemplateName}; the queued message will use the recurring drain",
                    message.TemplateName);
            }
        }
    }

    /// <summary>
    /// Google Postmaster Feedback-ID: <c>templateName:campaignId-or-none:category:humans-nobodies</c>. Campaign id,
    /// not grant id, so it aggregates across recipients. Google requires a 5-15 char SenderId.
    /// </summary>
    private static string BuildFeedbackId(EmailMessage message, MessageCategory? category) =>
        $"{message.TemplateName}:{message.CampaignId?.ToString() ?? "none"}:{category?.ToString() ?? "none"}:humans-nobodies";
}
