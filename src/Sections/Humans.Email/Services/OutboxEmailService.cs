using System.Text.Json;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Services;

/// <summary>
/// Application-layer implementation of <see cref="IEmailService"/>: the single
/// transport path for outbound email. Given a fully-rendered
/// <see cref="EmailMessage"/> (built by <see cref="IEmailMessageFactory"/>), it
/// applies opt-out suppression and List-Unsubscribe headers for opt-outable
/// categories, wraps the body with <see cref="IEmailBodyComposer"/>, appends a row
/// to the outbox through <see cref="IEmailOutboxRepository"/>, records the
/// per-template metric, and — for time-sensitive templates that set
/// <see cref="EmailMessage.TriggerImmediate"/> — runs the processor immediately
/// through <see cref="IImmediateOutboxProcessor"/>. SMTP-send lives in
/// <c>ProcessEmailOutboxJob</c> — except for <see cref="EmailMessage.DoNotPersist"/>
/// messages, which go straight to <see cref="IEmailTransport"/> here because they
/// must leave no stored copy of the recipient.
/// </summary>
internal sealed class OutboxEmailService(
    IEmailOutboxRepository outboxRepo,
    IUserEmailService userEmailService,
    IEmailBodyComposer bodyComposer,
    IImmediateOutboxProcessor immediateProcessor,
    IEmailTransport transport,
    IHumansMetrics metrics,
    IClock clock,
    ICommunicationPreferenceService commPrefService,
    ILogger<OutboxEmailService> logger) : IEmailService
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Explicit UserId wins (the campaign-code path supplies the grant's user);
        // otherwise resolve from the verified recipient address (Profile §2c).
        var userId = message.UserId
            ?? (await userEmailService.FindByAddressAsync(
                    message.RecipientEmail, aliased: false, verifiedOnly: true, cancellationToken))
                .FirstOrDefault()?.UserId;

        var category = message.Category;

        // null / always-on (System, CampaignCodes) ⇒ always send: no opt-out
        // suppression and no unsubscribe (there is nothing for it to do).
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

        var (wrappedHtml, plainText) = bodyComposer.Compose(message.HtmlBody, unsubscribeUrl);

        if (message.DoNotPersist)
        {
            // Straight to the transport: no row, so no retry and no stored copy of a
            // recipient the erasure cascade has already removed. Logged without the
            // address for the same reason.
            await transport.SendAsync(
                message.RecipientEmail, message.RecipientName, message.Subject,
                wrappedHtml, plainText, message.ReplyTo, headers, cancellationToken: cancellationToken);

            metrics.RecordEmailQueued(message.TemplateName);
            logger.LogInformation(
                "Email sent without an outbox row: {TemplateName}", message.TemplateName);
            return;
        }

        var entity = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = message.RecipientEmail,
            RecipientName = message.RecipientName,
            Subject = message.Subject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = message.TemplateName,
            UserId = userId,
            CampaignGrantId = message.CampaignGrantId,
            ReplyTo = message.ReplyTo,
            ExtraHeaders = extraHeadersJson,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = clock.GetCurrentInstant()
        };

        await outboxRepo.AddAsync(entity, cancellationToken);

        metrics.RecordEmailQueued(message.TemplateName);
        logger.LogInformation("Email queued: {TemplateName} to {Recipient}", message.TemplateName, message.RecipientEmail);

        if (message.TriggerImmediate)
        {
            immediateProcessor.TriggerImmediate();
            logger.LogInformation("Triggered immediate outbox processing for {TemplateName}", message.TemplateName);
        }
    }

    /// <summary>
    /// Google Postmaster Feedback-ID: <c>templateName:campaignId-or-none:category:humans-nobodies</c>.
    /// Template name is the primary identifier; campaign id (shared by every grant in
    /// the campaign — <see cref="EmailMessage.CampaignGrantId"/> is per-recipient and
    /// would not aggregate) and category refine it. SenderId (<c>humans-nobodies</c>,
    /// 15 chars — within Google's 5-15 char SenderId requirement) is last and constant.
    /// </summary>
    private static string BuildFeedbackId(EmailMessage message, MessageCategory? category) =>
        $"{message.TemplateName}:{message.CampaignId?.ToString() ?? "none"}:{category?.ToString() ?? "none"}:humans-nobodies";
}
