using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Email;

/// <summary>
/// Service for sending email notifications.
/// </summary>
public interface IEmailService : IApplicationService
{
    /// <summary>
    /// Sends an application approved notification to the applicant.
    /// </summary>
    /// <param name="userEmail">The applicant's email.</param>
    /// <param name="userName">The applicant's name.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an application rejected notification to the applicant.
    /// </summary>
    /// <param name="userEmail">The applicant's email.</param>
    /// <param name="userName">The applicant's name.</param>
    /// <param name="tier">The membership tier applied for.</param>
    /// <param name="reason">The reason for rejection.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a legal document updated notification requiring re-consent.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="documentName">The document name.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification for multiple legal document updates requiring re-consent.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="documentNames">The names of updated documents.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a re-consent reminder before access is suspended.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="documentNames">Names of documents requiring consent.</param>
    /// <param name="daysRemaining">Days remaining before suspension.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a welcome email to a new member.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an access suspended notification.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="reason">The reason for suspension.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an email verification link.
    /// </summary>
    /// <param name="toEmail">The email address to verify.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="verificationUrl">The URL to verify the email.</param>
    /// <param name="isConflict">True if this email belongs to another account and verifying will trigger a merge request.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        bool isConflict = false,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends confirmation that account deletion has been requested.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="deletionDate">When the account will be deleted.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends confirmation that account has been deleted.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification that the user has been added to a team.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="teamName">The team name.</param>
    /// <param name="teamSlug">The team's URL slug (used to construct the team page link).</param>
    /// <param name="resources">Google resources associated with the team (name + URL pairs).</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAddedToTeamAsync(
        string userEmail,
        string userName,
        string teamName,
        string teamSlug,
        IEnumerable<(string Name, string? Url)> resources,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a signup rejection notification to the user.
    /// This is for rejecting a human's signup/profile (not an Asociado application).
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's display name.</param>
    /// <param name="reason">The reason for rejection.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendSignupRejectedAsync(
        string userEmail,
        string userName,
        string? reason,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a term renewal reminder to a Colaborador or Asociado whose term is expiring soon.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's display name.</param>
    /// <param name="tierName">The membership tier name (e.g. "Colaborador", "Asociado").</param>
    /// <param name="expiresAt">The term expiry date.</param>
    /// <param name="culture">The recipient's preferred culture (ISO code, e.g. "es").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendTermRenewalReminderAsync(
        string userEmail,
        string userName,
        string tierName,
        string expiresAt,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a feedback response notification to the reporter.
    /// </summary>
    Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string reportLink, string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a facilitated message from one volunteer to another.
    /// </summary>
    Task SendFacilitatedMessageAsync(
        string recipientEmail,
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a personalized "email a rota" message from the rota's coordinator
    /// to a single signup on the rota. The body carries the coordinator's free-text
    /// message plus the recipient's own chronologically-ordered shift list on this
    /// rota. Category is <see cref="MessageCategory.VolunteerUpdates"/>; replies go
    /// to the coordinator's email when supplied.
    /// </summary>
    Task SendCoordinatorRotaMessageAsync(
        CoordinatorRotaMessageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a magic link login email to an existing user.
    /// </summary>
    Task SendMagicLinkLoginAsync(
        string toEmail,
        string displayName,
        string magicLinkUrl,
        string? culture = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a magic link signup email to a new user.
    /// </summary>
    Task SendMagicLinkSignupAsync(
        string toEmail,
        string magicLinkUrl,
        string? culture = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends workspace credentials to the user's recovery email after provisioning a @nobodies.team account.
    /// </summary>
    Task SendWorkspaceCredentialsAsync(
        string recoveryEmail,
        string userName,
        string workspaceEmail,
        string tempPassword,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification to an issue reporter that an admin has commented
    /// on their issue. Phase 5 of the Issues section feature wires the body
    /// and templates; the interface is added in Phase 4 so
    /// <see cref="Humans.Application.Services.Issues"/> can compile and be
    /// covered by unit tests.
    /// </summary>
    Task SendIssueCommentAsync(
        string to,
        string displayName,
        string issueTitle,
        string commentContent,
        string issueLink,
        string preferredLanguage,
        CancellationToken ct = default);

    /// <summary>
    /// Enqueue a campaign code email to a recipient. Campaign content (subject and
    /// markdown body with {{Code}}/{{Name}} placeholders) is rendered and wrapped in
    /// the system email template by the outbox service, then linked to the grant
    /// via <see cref="CampaignCodeEmailRequest.CampaignGrantId"/> for status tracking.
    /// </summary>
    Task SendCampaignCodeAsync(CampaignCodeEmailRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an event lifecycle notification (submitted / approved / rejected /
    /// resubmit-requested) — dispatches on <see cref="EventLifecycleNotification.NewStatus"/>
    /// to pick the matching template.
    /// </summary>
    Task SendEventLifecycleNotificationAsync(
        EventLifecycleNotification request,
        string userEmail,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification that a Google Group membership has been removed
    /// (Variant 1 — full loss of access, group sub-template). System category;
    /// no unsubscribe footer (issue peterdrier/Humans#639).
    /// </summary>
    Task SendGoogleGroupRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string groupName,
        string groupEmail,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification that a Google Drive permission has been removed
    /// (Variant 1 — full loss of access, Drive sub-template). System category;
    /// no unsubscribe footer (issue peterdrier/Humans#639).
    /// </summary>
    Task SendGoogleDriveRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string folderName,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification that a Google Workspace permission has been
    /// removed from a secondary email address (Variant 2 — secondary-email
    /// cleanup). The user retains access through their primary Google
    /// address. System category; no unsubscribe footer
    /// (issue peterdrier/Humans#639).
    /// </summary>
    Task SendGoogleAccessRemovalSecondaryCleanupAsync(
        string removedEmail,
        string userName,
        string currentGoogleEmail,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirmation to the Sender that their ticket-transfer request was received
    /// and the ticket team will process it.
    /// </summary>
    Task SendTicketTransferRequestedAsync(
        string senderEmail,
        string senderName,
        string receiverName,
        string ticketLabel,
        string? culture = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Action-needed notification to the ticket team inbox (tickets@) that a new
    /// transfer request is awaiting manual processing. Always English (admin).
    /// </summary>
    Task SendTicketTransferTeamNotificationAsync(
        string senderName,
        string receiverName,
        string receiverEmail,
        string ticketLabel,
        string? reason,
        string reviewUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decision notification sent to both Sender and Receiver: the transfer was
    /// completed (<paramref name="successful"/> true) or cancelled with a reason.
    /// </summary>
    Task SendTicketTransferDecisionAsync(
        string toEmail,
        string toName,
        bool successful,
        string ticketLabel,
        string receiverName,
        string? reason,
        string? culture = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload for a coordinator "email a rota" message to a single signup.
/// <see cref="ShiftLines"/> are pre-rendered, chronologically-ordered shift labels
/// for the recipient on this rota (e.g. "Mon July 6 @ 19:30") — the renderer
/// HTML-encodes them, it does not parse or sort them.
/// </summary>
public record CoordinatorRotaMessageRequest(
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string RotaName,
    string MessageText,
    IReadOnlyList<string> ShiftLines,
    string? Culture = null);

/// <summary>
/// Payload for enqueuing a campaign-code email.
/// </summary>
public record CampaignCodeEmailRequest(
    Guid UserId,
    Guid CampaignGrantId,
    string RecipientEmail,
    string RecipientName,
    string Subject,
    string MarkdownBody,
    string Code,
    string? ReplyTo);

/// <summary>
/// Payload for an event lifecycle notification. <see cref="NewStatus"/> picks
/// the template: <see cref="EventStatus.Pending"/> = submission received,
/// <see cref="EventStatus.Approved"/> = approved, <see cref="EventStatus.Rejected"/>
/// = rejected (requires <see cref="Reason"/> and <see cref="ActionUrl"/> for the
/// edit link), <see cref="EventStatus.ResubmitRequested"/> = changes requested
/// (also requires <see cref="Reason"/> and <see cref="ActionUrl"/>).
/// </summary>
public record EventLifecycleNotification(
    EventStatus NewStatus,
    string UserName,
    string EventTitle,
    string? Reason = null,
    string? ActionUrl = null,
    string? Culture = null)
{
    public string TemplateName() => NewStatus switch
    {
        EventStatus.Pending => "event_submitted",
        EventStatus.Approved => "event_approved",
        EventStatus.Rejected => "event_rejected",
        EventStatus.ResubmitRequested => "event_resubmit_requested",
        _ => throw new InvalidOperationException(
            $"EventLifecycleNotification does not support status {NewStatus}")
    };
}
