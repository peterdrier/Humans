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
            ?? await userEmailService.GetUserIdByVerifiedEmailAsync(message.RecipientEmail, cancellationToken);

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
        string? extraHeadersJson = null;
        if (optOutEligible && userId.HasValue)
        {
            var headers = commPrefService.GenerateUnsubscribeHeaders(userId.Value, category!.Value);
            extraHeadersJson = JsonSerializer.Serialize(headers);
            unsubscribeUrl = commPrefService.GenerateBrowserUnsubscribeUrl(userId.Value, category.Value);
        }

        var (wrappedHtml, plainText) = bodyComposer.Compose(message.HtmlBody, unsubscribeUrl);

        if (message.DoNotPersist)
        {
            // Straight to the transport: no row, so no retry and no stored copy of a
            // recipient the erasure cascade has already removed. Logged without the
            // address for the same reason.
            await transport.SendAsync(
                message.RecipientEmail, message.RecipientName, message.Subject,
                wrappedHtml, plainText, message.ReplyTo, cancellationToken: cancellationToken);

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
}
