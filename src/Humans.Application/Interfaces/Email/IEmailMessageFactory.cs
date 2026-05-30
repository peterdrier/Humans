using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Email;

/// <summary>
/// Builds a fully-rendered <see cref="EmailMessage"/> for each system email type.
/// This is the typed seam between the pure <see cref="IEmailRenderer"/> (subject +
/// HTML only) and the single <see cref="IEmailService.SendAsync"/> transport: each
/// method renders its content and stamps the routing policy (template name, opt-out
/// category, reply-to, immediate-drain, and — for campaign codes — the explicit
/// user and grant ids). Keeping one typed method per domain verb preserves
/// compile-time safety; the policy lives here, never in the renderer and never at
/// the call sites.
/// </summary>
public interface IEmailMessageFactory
{
    /// <summary>Application approved notification (Governance).</summary>
    EmailMessage ApplicationApproved(string userEmail, string userName, MembershipTier tier, string? culture = null);

    /// <summary>Application rejected notification (Governance).</summary>
    EmailMessage ApplicationRejected(string userEmail, string userName, MembershipTier tier, string reason, string? culture = null);

    /// <summary>Re-consent required notification (always-send).</summary>
    EmailMessage ReConsentsRequired(string userEmail, string userName, IEnumerable<string> documentNames, string? culture = null);

    /// <summary>Re-consent reminder before suspension (always-send).</summary>
    EmailMessage ReConsentReminder(string userEmail, string userName, IEnumerable<string> documentNames, int daysRemaining, string? culture = null);

    /// <summary>Access suspended notification (always-send).</summary>
    EmailMessage AccessSuspended(string userEmail, string userName, string reason, string? culture = null);

    /// <summary>Email verification link (always-send, immediate drain).</summary>
    EmailMessage EmailVerification(string toEmail, string userName, string verificationUrl, bool isConflict = false, string? culture = null);

    /// <summary>Account deletion requested confirmation (always-send).</summary>
    EmailMessage AccountDeletionRequested(string userEmail, string userName, Instant deletionDate, string? culture = null);

    /// <summary>Account deleted confirmation (always-send).</summary>
    EmailMessage AccountDeleted(string userEmail, string userName, string? culture = null);

    /// <summary>Added-to-team notification (TeamUpdates).</summary>
    EmailMessage AddedToTeam(string userEmail, string userName, string teamName, string teamSlug, IEnumerable<(string Name, string? Url)> resources, string? culture = null);

    /// <summary>Signup rejection notification (System).</summary>
    EmailMessage SignupRejected(string userEmail, string userName, string? reason, string? culture = null);

    /// <summary>Term renewal reminder (Governance).</summary>
    EmailMessage TermRenewalReminder(string userEmail, string userName, string tierName, string expiresAt, string? culture = null);

    /// <summary>Feedback response notification (System).</summary>
    EmailMessage FeedbackResponse(string userEmail, string userName, string originalDescription, string responseMessage, string reportLink, string? culture = null);

    /// <summary>Facilitated volunteer-to-volunteer message (FacilitatedMessages); reply-to is the sender when contact info is shared.</summary>
    EmailMessage FacilitatedMessage(string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null);

    /// <summary>Coordinator "email a rota" message to one signup (VolunteerUpdates); reply-to is the coordinator.</summary>
    EmailMessage CoordinatorRotaMessage(CoordinatorRotaMessageRequest request);

    /// <summary>Coordinator team-level "email all rotas" message to one signup (VolunteerUpdates); reply-to is the coordinator.</summary>
    EmailMessage CoordinatorTeamRotasMessage(CoordinatorTeamRotasMessageRequest request);

    /// <summary>Magic-link login email (always-send, immediate drain).</summary>
    EmailMessage MagicLinkLogin(string toEmail, string displayName, string magicLinkUrl, string? culture = null);

    /// <summary>Magic-link signup email (always-send, immediate drain).</summary>
    EmailMessage MagicLinkSignup(string toEmail, string magicLinkUrl, string? culture = null);

    /// <summary>Workspace credentials email (always-send, immediate drain).</summary>
    EmailMessage WorkspaceCredentials(string recoveryEmail, string userName, string workspaceEmail, string tempPassword, string? culture = null);

    /// <summary>Issue-comment notification to the reporter (System).</summary>
    EmailMessage IssueComment(string to, string displayName, string issueTitle, string commentContent, string issueLink, string preferredLanguage);

    /// <summary>Campaign-code email (CampaignCodes); carries the explicit user and grant ids and the reply-to from the request.</summary>
    EmailMessage CampaignCode(CampaignCodeEmailRequest request);

    /// <summary>Event lifecycle notification (always-send, immediate drain); template is chosen from the request status.</summary>
    EmailMessage EventLifecycle(EventLifecycleNotification request, string userEmail);

    /// <summary>Google Group removal — loss of access (System; no unsubscribe footer).</summary>
    EmailMessage GoogleGroupRemovalLossOfAccess(string removedEmail, string userName, string groupName, string groupEmail, string? culture = null);

    /// <summary>Google Drive removal — loss of access (System; no unsubscribe footer).</summary>
    EmailMessage GoogleDriveRemovalLossOfAccess(string removedEmail, string userName, string folderName, string? culture = null);

    /// <summary>Google secondary-email cleanup notification (System; no unsubscribe footer).</summary>
    EmailMessage GoogleAccessRemovalSecondaryCleanup(string removedEmail, string userName, string currentGoogleEmail, string? culture = null);

    /// <summary>Ticket-transfer request confirmation to the sender (System).</summary>
    EmailMessage TicketTransferRequested(string senderEmail, string senderName, string receiverName, string ticketLabel, string? culture = null);

    /// <summary>Ticket-transfer action-needed notice to the ticket team inbox (System, English).</summary>
    EmailMessage TicketTransferTeamNotification(string senderName, string receiverName, string receiverEmail, string ticketLabel, string? reason, string reviewUrl);

    /// <summary>Ticket-transfer decision (completed or cancelled) to sender or receiver (System).</summary>
    EmailMessage TicketTransferDecision(string toEmail, string toName, bool successful, string ticketLabel, string receiverName, string? reason, string? culture = null);
}
