using Humans.Application.Interfaces.Email;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Text;

namespace Humans.Application.Services.Email;

/// <summary>
/// Default <see cref="IEmailMessageFactory"/>: renders each system email through the
/// pure <see cref="IEmailRenderer"/> and stamps the routing policy that the
/// <see cref="IEmailService"/> transport reads (template name, opt-out category,
/// reply-to, immediate-drain, campaign user/grant ids). Pure mapping — no I/O.
/// </summary>
public sealed class EmailMessageFactory(IEmailRenderer renderer) : IEmailMessageFactory
{
    // Invariant long-date formatter; duplicates Infrastructure.EmailDateTimeExtensions's format to avoid a back-reference into Infrastructure.
    private static readonly LocalDatePattern InvariantLongDatePattern =
        LocalDatePattern.CreateWithInvariantCulture("MMMM d, yyyy");

    /// <inheritdoc />
    public EmailMessage ApplicationApproved(string userEmail, string userName, MembershipTier tier, string? culture = null)
    {
        var content = renderer.RenderApplicationApproved(userName, tier, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "application_approved", MessageCategory.Governance);
    }

    /// <inheritdoc />
    public EmailMessage ApplicationRejected(string userEmail, string userName, MembershipTier tier, string reason, string? culture = null)
    {
        var content = renderer.RenderApplicationRejected(userName, tier, reason, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "application_rejected", MessageCategory.Governance);
    }

    /// <inheritdoc />
    public EmailMessage ReConsentsRequired(string userEmail, string userName, IEnumerable<string> documentNames, string? culture = null)
    {
        var content = renderer.RenderReConsentsRequired(userName, documentNames.ToList(), culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "reconsents_required");
    }

    /// <inheritdoc />
    public EmailMessage ReConsentReminder(string userEmail, string userName, IEnumerable<string> documentNames, int daysRemaining, string? culture = null)
    {
        var content = renderer.RenderReConsentReminder(userName, documentNames.ToList(), daysRemaining, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "reconsent_reminder");
    }

    /// <inheritdoc />
    public EmailMessage AccessSuspended(string userEmail, string userName, string reason, string? culture = null)
    {
        var content = renderer.RenderAccessSuspended(userName, reason, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "access_suspended");
    }

    /// <inheritdoc />
    public EmailMessage EmailVerification(string toEmail, string userName, string verificationUrl, bool isConflict = false, string? culture = null)
    {
        var content = renderer.RenderEmailVerification(userName, toEmail, verificationUrl, isConflict, culture);
        return new EmailMessage(toEmail, userName, content.Subject, content.HtmlBody,
            "email_verification", TriggerImmediate: true);
    }

    /// <inheritdoc />
    public EmailMessage AccountDeletionRequested(string userEmail, string userName, Instant deletionDate, string? culture = null)
    {
        var formattedDate = InvariantLongDatePattern.Format(deletionDate.InUtc().Date);
        var content = renderer.RenderAccountDeletionRequested(userName, formattedDate, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "deletion_requested");
    }

    /// <inheritdoc />
    public EmailMessage AccountDeleted(string userEmail, string userName, string? culture = null)
    {
        var content = renderer.RenderAccountDeleted(userName, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "account_deleted");
    }

    /// <inheritdoc />
    public EmailMessage AddedToTeam(string userEmail, string userName, string teamName, string teamSlug, IEnumerable<(string Name, string? Url)> resources, string? culture = null)
    {
        var content = renderer.RenderAddedToTeam(userName, teamName, teamSlug, resources.ToList(), culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "added_to_team", MessageCategory.TeamUpdates);
    }

    /// <inheritdoc />
    public EmailMessage SignupRejected(string userEmail, string userName, string? reason, string? culture = null)
    {
        var content = renderer.RenderSignupRejected(userName, reason, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "signup_rejected", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage TermRenewalReminder(string userEmail, string userName, string tierName, string expiresAt, string? culture = null)
    {
        var content = renderer.RenderTermRenewalReminder(userName, tierName, expiresAt, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "term_renewal_reminder", MessageCategory.Governance);
    }

    /// <inheritdoc />
    public EmailMessage FeedbackResponse(string userEmail, string userName, string originalDescription, string responseMessage, string reportLink, string? culture = null)
    {
        var content = renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, reportLink, culture);
        return new EmailMessage(userEmail, userName, content.Subject, content.HtmlBody,
            "feedback_response", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage FacilitatedMessage(string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null)
    {
        var content = renderer.RenderFacilitatedMessage(recipientName, senderName, messageText, includeContactInfo, senderEmail, culture);
        var replyTo = includeContactInfo ? senderEmail : null;
        return new EmailMessage(recipientEmail, recipientName, content.Subject, content.HtmlBody,
            "facilitated_message", MessageCategory.FacilitatedMessages, ReplyTo: replyTo);
    }

    /// <inheritdoc />
    public EmailMessage CoordinatorRotaMessage(CoordinatorRotaMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = renderer.RenderCoordinatorRotaMessage(
            request.RecipientName, request.SenderName, request.SenderEmail,
            request.RotaName, request.MessageText, request.ShiftLines, request.Culture);
        return new EmailMessage(request.RecipientEmail, request.RecipientName, content.Subject, content.HtmlBody,
            "coordinator_rota_message", MessageCategory.VolunteerUpdates, ReplyTo: request.SenderEmail);
    }

    /// <inheritdoc />
    public EmailMessage CoordinatorTeamRotasMessage(CoordinatorTeamRotasMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = renderer.RenderCoordinatorTeamRotasMessage(
            request.RecipientName, request.SenderName, request.SenderEmail,
            request.TeamName, request.MessageText, request.ShiftGroups, request.Culture);
        return new EmailMessage(request.RecipientEmail, request.RecipientName, content.Subject, content.HtmlBody,
            "coordinator_team_rotas_message", MessageCategory.VolunteerUpdates, ReplyTo: request.SenderEmail);
    }

    /// <inheritdoc />
    public EmailMessage MagicLinkLogin(string toEmail, string displayName, string magicLinkUrl, string? culture = null)
    {
        var content = renderer.RenderMagicLinkLogin(displayName, magicLinkUrl, culture);
        return new EmailMessage(toEmail, displayName, content.Subject, content.HtmlBody,
            "magic_link_login", TriggerImmediate: true);
    }

    /// <inheritdoc />
    public EmailMessage MagicLinkSignup(string toEmail, string magicLinkUrl, string? culture = null)
    {
        var content = renderer.RenderMagicLinkSignup(magicLinkUrl, culture);
        return new EmailMessage(toEmail, toEmail, content.Subject, content.HtmlBody,
            "magic_link_signup", TriggerImmediate: true);
    }

    /// <inheritdoc />
    public EmailMessage WorkspaceCredentials(string recoveryEmail, string userName, string workspaceEmail, string tempPassword, string? culture = null)
    {
        var content = renderer.RenderWorkspaceCredentials(userName, workspaceEmail, tempPassword, culture);
        return new EmailMessage(recoveryEmail, userName, content.Subject, content.HtmlBody,
            "workspace_credentials", TriggerImmediate: true);
    }

    /// <inheritdoc />
    public EmailMessage IssueComment(string to, string displayName, string issueTitle, string commentContent, string issueLink, string preferredLanguage)
    {
        var content = renderer.RenderIssueComment(displayName, issueTitle, commentContent, issueLink, preferredLanguage);
        return new EmailMessage(to, displayName, content.Subject, content.HtmlBody,
            "issue_comment", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage CampaignCode(CampaignCodeEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Renderer HTML-encodes {{Code}}/{{Name}} substitutions to prevent injection.
        var content = renderer.RenderCampaignCode(request.Subject, request.MarkdownBody, request.Code, request.RecipientName);
        return new EmailMessage(request.RecipientEmail, request.RecipientName, content.Subject, content.HtmlBody,
            "campaign_code", MessageCategory.CampaignCodes,
            ReplyTo: request.ReplyTo, UserId: request.UserId, CampaignGrantId: request.CampaignGrantId);
    }

    /// <inheritdoc />
    public EmailMessage EventLifecycle(EventLifecycleNotification request, string userEmail)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = renderer.RenderEventLifecycle(request);
        return new EmailMessage(userEmail, request.UserName, content.Subject, content.HtmlBody,
            request.TemplateName(), TriggerImmediate: true);
    }

    /// <inheritdoc />
    public EmailMessage GoogleGroupRemovalLossOfAccess(string removedEmail, string userName, string groupName, string groupEmail, string? culture = null)
    {
        var content = renderer.RenderGoogleGroupRemovalLossOfAccess(userName, groupName, groupEmail, culture);
        return new EmailMessage(removedEmail, userName, content.Subject, content.HtmlBody,
            "google_group_removal_loss_of_access", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage GoogleDriveRemovalLossOfAccess(string removedEmail, string userName, string folderName, string? culture = null)
    {
        var content = renderer.RenderGoogleDriveRemovalLossOfAccess(userName, folderName, culture);
        return new EmailMessage(removedEmail, userName, content.Subject, content.HtmlBody,
            "google_drive_removal_loss_of_access", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage GoogleAccessRemovalSecondaryCleanup(string removedEmail, string userName, string currentGoogleEmail, string? culture = null)
    {
        var content = renderer.RenderGoogleAccessRemovalSecondaryCleanup(userName, removedEmail, currentGoogleEmail, culture);
        return new EmailMessage(removedEmail, userName, content.Subject, content.HtmlBody,
            "google_access_removal_secondary_cleanup", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage TicketTransferRequested(string senderEmail, string senderName, string receiverName, string ticketLabel, string? culture = null)
    {
        var content = renderer.RenderTicketTransferRequested(senderName, receiverName, ticketLabel, culture);
        return new EmailMessage(senderEmail, senderName, content.Subject, content.HtmlBody,
            "ticket_transfer_requested", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage TicketTransferTeamNotification(string senderName, string receiverName, string receiverEmail, string ticketLabel, string? reason, string reviewUrl)
    {
        var content = renderer.RenderTicketTransferTeamNotification(senderName, receiverName, receiverEmail, ticketLabel, reason, reviewUrl);
        return new EmailMessage(TicketConstants.TicketsTeamEmail, "Ticket team", content.Subject, content.HtmlBody,
            "ticket_transfer_team", MessageCategory.System);
    }

    /// <inheritdoc />
    public EmailMessage TicketTransferDecision(string toEmail, string toName, bool successful, string ticketLabel, string receiverName, string? reason, string? culture = null)
    {
        var content = renderer.RenderTicketTransferDecision(toName, successful, ticketLabel, receiverName, reason, culture);
        return new EmailMessage(toEmail, toName, content.Subject, content.HtmlBody,
            successful ? "ticket_transfer_completed" : "ticket_transfer_cancelled", MessageCategory.System);
    }
}
