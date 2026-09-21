using Humans.Users.Contracts;
using Humans.Email.Contracts;


namespace Humans.Email.Services;

/// <summary>
/// Rendered email content (subject + HTML body).
/// </summary>
internal sealed record EmailContent(string Subject, string HtmlBody);

/// <summary>
/// Renders email subject and body HTML for all system email types.
/// Separated from IEmailService (SMTP transport) to enable reuse by admin previews
/// and to centralize localization of email body text.
/// </summary>
internal interface IEmailRenderer
{
    /// <summary>
    /// Application submitted notification (to admin, always English).
    /// </summary>
    EmailContent RenderApplicationSubmitted(Guid applicationId, string applicantName);

    /// <summary>
    /// Re-consent required notification (one or more documents).
    /// </summary>
    EmailContent RenderReConsentsRequired(string userName, IReadOnlyList<string> documentNames, string? culture = null);

    /// <summary>
    /// Re-consent reminder before suspension.
    /// </summary>
    EmailContent RenderReConsentReminder(string userName, IReadOnlyList<string> documentNames, int daysRemaining, string? culture = null);

    /// <summary>
    /// Welcome email for new humans.
    /// </summary>
    EmailContent RenderWelcome(string userName, string? culture = null);

    /// <summary>
    /// Access suspended notification.
    /// </summary>
    EmailContent RenderAccessSuspended(string userName, string reason, string? culture = null);

    /// <summary>
    /// Email verification link.
    /// </summary>
    EmailContent RenderEmailVerification(string userName, string toEmail, string verificationUrl, bool isConflict = false, string? culture = null);

    /// <summary>
    /// Account deletion requested confirmation.
    /// </summary>
    EmailContent RenderAccountDeletionRequested(string userName, string formattedDeletionDate, string? culture = null);

    /// <summary>
    /// Account deleted confirmation.
    /// </summary>
    EmailContent RenderAccountDeleted(string userName, string? culture = null);

    /// <summary>
    /// Survey invitation — links to the tokenised answering wizard and optionally replaces the
    /// standard localized subject/message with safely rendered author copy.
    /// </summary>
    EmailContent RenderSurveyInvitation(
        string userName,
        string surveyTitle,
        string answerToken,
        string? culture = null,
        string? customSubject = null,
        string? customMessage = null);

    /// <summary>Survey reminder — single nudge for an unfinished invitation.</summary>
    EmailContent RenderSurveyReminder(string userName, string surveyTitle, string answerToken, string? culture = null);

    /// <summary>
    /// Facilitated message between volunteers.
    /// </summary>
    EmailContent RenderFacilitatedMessage(
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null);

    /// <summary>
    /// "Email a rota" message from the coordinator to a single signup on the
    /// rota. Body contains the coordinator's free-text message plus the
    /// recipient's chronologically-ordered shift list on this rota.
    /// </summary>
    EmailContent RenderCoordinatorRotaMessage(
        string recipientName,
        string senderName,
        string? senderEmail,
        string rotaName,
        string messageText,
        IReadOnlyList<string> shiftLines,
        string? culture = null);

    /// <summary>
    /// Team-level "email all rotas" message from a coordinator to a single signup.
    /// Body contains the coordinator's free-text message plus the recipient's
    /// shifts grouped by rota (each group already chronological and formatted in
    /// the rota's timezone).
    /// </summary>
    EmailContent RenderCoordinatorTeamRotasMessage(
        string recipientName,
        string senderName,
        string? senderEmail,
        string teamName,
        string messageText,
        IReadOnlyList<RotaShiftGroup> shiftGroups,
        string? culture = null);

    /// <summary>
    /// Magic link login email for an existing user.
    /// </summary>
    EmailContent RenderMagicLinkLogin(string displayName, string magicLinkUrl, string? culture = null);

    /// <summary>
    /// Magic link signup email for a new user.
    /// </summary>
    EmailContent RenderMagicLinkSignup(string magicLinkUrl, string? culture = null);

    /// <summary>
    /// Workspace credentials email sent after provisioning a @nobodies.team account.
    /// </summary>
    EmailContent RenderWorkspaceCredentials(string userName, string workspaceEmail, string tempPassword, string? culture = null);

    /// <summary>
    /// Variant 1 group sub-template — Google Group removal, loss of access
    /// (issue peterdrier/Humans#639).
    /// </summary>
    EmailContent RenderGoogleGroupRemovalLossOfAccess(
        string userName,
        string groupName,
        string groupEmail,
        string? culture = null);

    /// <summary>
    /// Variant 1 Drive sub-template — Google Drive permission removal, loss
    /// of access (issue peterdrier/Humans#639).
    /// </summary>
    EmailContent RenderGoogleDriveRemovalLossOfAccess(
        string userName,
        string folderName,
        string? culture = null);

    /// <summary>
    /// Variant 2 — secondary-email cleanup. Same template covers both
    /// Group and Drive removals; the message is reassurance-focused, not
    /// resource-specific (issue peterdrier/Humans#639).
    /// </summary>
    EmailContent RenderGoogleAccessRemovalSecondaryCleanup(
        string userName,
        string removedEmail,
        string currentGoogleEmail,
        string? culture = null);

    /// <summary>Ticket-transfer request confirmation to the Sender.</summary>
    EmailContent RenderTicketTransferRequested(
        string senderName, string receiverName, string ticketLabel, string? culture = null);

    /// <summary>Ticket-transfer action-needed notice to the ticket team (English).</summary>
    EmailContent RenderTicketTransferTeamNotification(
        string senderName, string receiverName, string receiverEmail,
        string ticketLabel, string? reason, string reviewUrl);

    /// <summary>Ticket-transfer decision (completed / cancelled-with-reason) to Sender + Receiver.</summary>
    EmailContent RenderTicketTransferDecision(
        string toName, bool successful, string ticketLabel, string receiverName,
        string? reason, string? culture = null);

    /// <summary>
    /// Working-group register notice — dispatches on <see cref="WorkgroupNoticeRequest.Kind"/>
    /// to render the matching template. Builds the working-group link from
    /// <see cref="WorkgroupNoticeRequest.WorkgroupSlug"/> the same way a team link is
    /// built from a team slug.
    /// </summary>
    EmailContent RenderWorkgroupNotice(WorkgroupNoticeRequest request);

}
