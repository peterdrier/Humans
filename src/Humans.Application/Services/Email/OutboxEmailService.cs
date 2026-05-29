using System.Globalization;
using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Email;

/// <summary>
/// Application-layer implementation of <see cref="IEmailService"/>: renders
/// each system email through <see cref="IEmailRenderer"/>, wraps the body
/// with <see cref="IEmailBodyComposer"/>, and appends a row to the outbox
/// through <see cref="IEmailOutboxRepository"/>. Time-sensitive templates
/// (email verification, magic-link, workspace credentials) also trigger an
/// immediate processor run through <see cref="IImmediateOutboxProcessor"/>.
/// SMTP-send lives in <c>ProcessEmailOutboxJob</c>.
/// </summary>
public sealed class OutboxEmailService(
    IEmailOutboxRepository outboxRepo,
    IUserEmailService userEmailService,
    IEmailRenderer renderer,
    IEmailBodyComposer bodyComposer,
    IImmediateOutboxProcessor immediateProcessor,
    IHumansMetrics metrics,
    IClock clock,
    ICommunicationPreferenceService commPrefService,
    ILogger<OutboxEmailService> logger) : IEmailService
{
    /// <inheritdoc />
    public async Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderApplicationApproved(userName, tier, culture);
        await EnqueueAsync(userEmail, userName, content, "application_approved", cancellationToken,
            category: MessageCategory.Governance);
    }

    /// <inheritdoc />
    public async Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderApplicationRejected(userName, tier, reason, culture);
        await EnqueueAsync(userEmail, userName, content, "application_rejected", cancellationToken,
            category: MessageCategory.Governance);
    }

    /// <inheritdoc />
    public async Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var docs = documentNames.ToList();
        var content = renderer.RenderReConsentsRequired(userName, docs, culture);
        await EnqueueAsync(userEmail, userName, content, "reconsents_required", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var docs = documentNames.ToList();
        var content = renderer.RenderReConsentReminder(userName, docs, daysRemaining, culture);
        await EnqueueAsync(userEmail, userName, content, "reconsent_reminder", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderAccessSuspended(userName, reason, culture);
        await EnqueueAsync(userEmail, userName, content, "access_suspended", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        bool isConflict = false,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderEmailVerification(userName, toEmail, verificationUrl, isConflict, culture);
        await EnqueueAsync(toEmail, userName, content, "email_verification", cancellationToken,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        // Invariant long-date formatter; duplicated from Infrastructure.EmailDateTimeExtensions to avoid back-reference.
        var formattedDate = deletionDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var content = renderer.RenderAccountDeletionRequested(userName, formattedDate, culture);
        await EnqueueAsync(userEmail, userName, content, "deletion_requested", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderAccountDeleted(userName, culture);
        await EnqueueAsync(userEmail, userName, content, "account_deleted", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAddedToTeamAsync(
        string userEmail,
        string userName,
        string teamName,
        string teamSlug,
        IEnumerable<(string Name, string? Url)> resources,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var resourceList = resources.ToList();
        var content = renderer.RenderAddedToTeam(userName, teamName, teamSlug, resourceList, culture);
        await EnqueueAsync(userEmail, userName, content, "added_to_team", cancellationToken,
            category: MessageCategory.TeamUpdates);
    }

    /// <inheritdoc />
    public async Task SendSignupRejectedAsync(
        string userEmail,
        string userName,
        string? reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderSignupRejected(userName, reason, culture);
        await EnqueueAsync(userEmail, userName, content, "signup_rejected", cancellationToken,
            category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendTermRenewalReminderAsync(
        string userEmail,
        string userName,
        string tierName,
        string expiresAt,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderTermRenewalReminder(userName, tierName, expiresAt, culture);
        await EnqueueAsync(userEmail, userName, content, "term_renewal_reminder", cancellationToken,
            category: MessageCategory.Governance);
    }

    /// <inheritdoc />
    public async Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string reportLink, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, reportLink, culture);
        await EnqueueAsync(userEmail, userName, content, "feedback_response", cancellationToken,
            category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendFacilitatedMessageAsync(
        string recipientEmail,
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderFacilitatedMessage(
            recipientName, senderName, messageText, includeContactInfo, senderEmail, culture);
        var replyTo = includeContactInfo ? senderEmail : null;
        await EnqueueAsync(recipientEmail, recipientName, content, "facilitated_message", cancellationToken,
            replyTo: replyTo, category: MessageCategory.FacilitatedMessages);
    }

    /// <inheritdoc />
    public async Task SendCoordinatorRotaMessageAsync(
        CoordinatorRotaMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = renderer.RenderCoordinatorRotaMessage(
            request.RecipientName,
            request.SenderName,
            request.SenderEmail,
            request.RotaName,
            request.MessageText,
            request.ShiftLines,
            request.Culture);

        await EnqueueAsync(
            request.RecipientEmail,
            request.RecipientName,
            content,
            "coordinator_rota_message",
            cancellationToken,
            replyTo: request.SenderEmail,
            category: MessageCategory.VolunteerUpdates);
    }

    /// <inheritdoc />
    public async Task SendCoordinatorTeamRotasMessageAsync(
        CoordinatorTeamRotasMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = renderer.RenderCoordinatorTeamRotasMessage(
            request.RecipientName,
            request.SenderName,
            request.SenderEmail,
            request.TeamName,
            request.MessageText,
            request.ShiftGroups,
            request.Culture);

        await EnqueueAsync(
            request.RecipientEmail,
            request.RecipientName,
            content,
            "coordinator_team_rotas_message",
            cancellationToken,
            replyTo: request.SenderEmail,
            category: MessageCategory.VolunteerUpdates);
    }

    /// <inheritdoc />
    public async Task SendMagicLinkLoginAsync(
        string toEmail,
        string displayName,
        string magicLinkUrl,
        string? culture = null,
        CancellationToken ct = default)
    {
        var content = renderer.RenderMagicLinkLogin(displayName, magicLinkUrl, culture);
        await EnqueueAsync(toEmail, displayName, content, "magic_link_login", ct,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendMagicLinkSignupAsync(
        string toEmail,
        string magicLinkUrl,
        string? culture = null,
        CancellationToken ct = default)
    {
        var content = renderer.RenderMagicLinkSignup(magicLinkUrl, culture);
        await EnqueueAsync(toEmail, toEmail, content, "magic_link_signup", ct,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendWorkspaceCredentialsAsync(
        string recoveryEmail,
        string userName,
        string workspaceEmail,
        string tempPassword,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderWorkspaceCredentials(userName, workspaceEmail, tempPassword, culture);
        await EnqueueAsync(recoveryEmail, userName, content, "workspace_credentials", cancellationToken,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendCampaignCodeAsync(CampaignCodeEmailRequest request, CancellationToken cancellationToken = default)
    {
        // Renderer HTML-encodes {{Code}}/{{Name}} substitutions to prevent injection.
        var rendered = renderer.RenderCampaignCode(
            request.Subject, request.MarkdownBody, request.Code, request.RecipientName);

        var unsubHeaders = commPrefService.GenerateUnsubscribeHeaders(request.UserId, MessageCategory.CampaignCodes);
        var extraHeadersJson = JsonSerializer.Serialize(unsubHeaders);
        var unsubscribeUrl = commPrefService.GenerateBrowserUnsubscribeUrl(request.UserId, MessageCategory.CampaignCodes);

        var (wrappedHtml, plainText) = bodyComposer.Compose(rendered.HtmlBody, unsubscribeUrl);

        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = request.RecipientEmail,
            RecipientName = request.RecipientName,
            Subject = rendered.Subject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = "campaign_code",
            UserId = request.UserId,
            CampaignGrantId = request.CampaignGrantId,
            ReplyTo = request.ReplyTo,
            ExtraHeaders = extraHeadersJson,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = clock.GetCurrentInstant()
        };

        await outboxRepo.AddAsync(message, cancellationToken);

        metrics.RecordEmailQueued("campaign_code");
        logger.LogInformation(
            "Campaign code email queued for grant {GrantId} to {Recipient}",
            request.CampaignGrantId, request.RecipientEmail);
    }

    /// <inheritdoc />
    public async Task SendGoogleGroupRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string groupName,
        string groupEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderGoogleGroupRemovalLossOfAccess(userName, groupName, groupEmail, culture);
        await EnqueueAsync(removedEmail, userName, content, "google_group_removal_loss_of_access", cancellationToken,
            category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendGoogleDriveRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string folderName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderGoogleDriveRemovalLossOfAccess(userName, folderName, culture);
        await EnqueueAsync(removedEmail, userName, content, "google_drive_removal_loss_of_access", cancellationToken,
            category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendGoogleAccessRemovalSecondaryCleanupAsync(
        string removedEmail,
        string userName,
        string currentGoogleEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderGoogleAccessRemovalSecondaryCleanup(
            userName, removedEmail, currentGoogleEmail, culture);
        await EnqueueAsync(removedEmail, userName, content, "google_access_removal_secondary_cleanup", cancellationToken,
            category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendIssueCommentAsync(
        string to,
        string displayName,
        string issueTitle,
        string commentContent,
        string issueLink,
        string preferredLanguage,
        CancellationToken ct = default)
    {
        var content = renderer.RenderIssueComment(displayName, issueTitle, commentContent, issueLink, preferredLanguage);
        await EnqueueAsync(to, displayName, content, "issue_comment", ct,
            category: MessageCategory.System);
    }

    private async Task EnqueueAsync(
        string recipientEmail,
        string recipientName,
        EmailContent content,
        string templateName,
        CancellationToken cancellationToken,
        bool triggerImmediate = false,
        string? replyTo = null,
        MessageCategory? category = null)
    {
        var userId = await userEmailService.GetUserIdByVerifiedEmailAsync(recipientEmail, cancellationToken);

        if (category is not null && category != MessageCategory.System && userId.HasValue)
        {
            if (await commPrefService.IsOptedOutAsync(userId.Value, category.Value, cancellationToken))
            {
                logger.LogInformation(
                    "Email suppressed: {TemplateName} to {Recipient} — opted out of {Category}",
                    templateName, recipientEmail, category.Value);
                return;
            }
        }

        string? unsubscribeUrl = null;
        string? extraHeadersJson = null;
        if (category is not null && category != MessageCategory.System && userId.HasValue)
        {
            var headers = commPrefService.GenerateUnsubscribeHeaders(userId.Value, category.Value);
            extraHeadersJson = JsonSerializer.Serialize(headers);
            unsubscribeUrl = commPrefService.GenerateBrowserUnsubscribeUrl(userId.Value, category.Value);
        }

        var (wrappedHtml, plainText) = bodyComposer.Compose(content.HtmlBody, unsubscribeUrl);

        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = recipientEmail,
            RecipientName = recipientName,
            Subject = content.Subject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = templateName,
            UserId = userId,
            ReplyTo = replyTo,
            ExtraHeaders = extraHeadersJson,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = clock.GetCurrentInstant()
        };

        await outboxRepo.AddAsync(message, cancellationToken);

        metrics.RecordEmailQueued(templateName);
        logger.LogInformation("Email queued: {TemplateName} to {Recipient}", templateName, recipientEmail);

        if (triggerImmediate)
        {
            immediateProcessor.TriggerImmediate();
            logger.LogInformation("Triggered immediate outbox processing for {TemplateName}", templateName);
        }
    }

    public async Task SendEventLifecycleNotificationAsync(
        EventLifecycleNotification request,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderEventLifecycle(request);
        await EnqueueAsync(userEmail, request.UserName, content,
            request.TemplateName(), cancellationToken,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendTicketTransferRequestedAsync(
        string senderEmail, string senderName, string receiverName, string ticketLabel,
        string? culture = null, CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderTicketTransferRequested(senderName, receiverName, ticketLabel, culture);
        await EnqueueAsync(senderEmail, senderName, content, "ticket_transfer_requested", cancellationToken,
            category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendTicketTransferTeamNotificationAsync(
        string senderName, string receiverName, string receiverEmail, string ticketLabel,
        string? reason, string reviewUrl, CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderTicketTransferTeamNotification(
            senderName, receiverName, receiverEmail, ticketLabel, reason, reviewUrl);
        await EnqueueAsync(TicketConstants.TicketsTeamEmail, "Ticket team", content,
            "ticket_transfer_team", cancellationToken, category: MessageCategory.System);
    }

    /// <inheritdoc />
    public async Task SendTicketTransferDecisionAsync(
        string toEmail, string toName, bool successful, string ticketLabel, string receiverName,
        string? reason, string? culture = null, CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderTicketTransferDecision(toName, successful, ticketLabel, receiverName, reason, culture);
        await EnqueueAsync(toEmail, toName, content,
            successful ? "ticket_transfer_completed" : "ticket_transfer_cancelled",
            cancellationToken, category: MessageCategory.System);
    }
}
