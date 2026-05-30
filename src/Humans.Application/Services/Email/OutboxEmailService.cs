using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Email;

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
/// <c>ProcessEmailOutboxJob</c>.
/// </summary>
public sealed class OutboxEmailService(
    IEmailOutboxRepository outboxRepo,
    IUserEmailService userEmailService,
    IEmailBodyComposer bodyComposer,
    IImmediateOutboxProcessor immediateProcessor,
    IHumansMetrics metrics,
    IClock clock,
    ICommunicationPreferenceService commPrefService,
    ILogger<OutboxEmailService> logger) : IEmailService
{
    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Explicit UserId wins (the campaign-code path supplies the grant's user);
        // otherwise resolve from the verified recipient address (Profile §2c).
        var userId = message.UserId
            ?? await userEmailService.GetUserIdByVerifiedEmailAsync(message.RecipientEmail, cancellationToken);

        var category = message.Category;

        // null / System ⇒ always send: no opt-out suppression and no unsubscribe.
        var optOutEligible = category is not null && category != MessageCategory.System;

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
