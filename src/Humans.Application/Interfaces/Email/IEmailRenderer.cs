using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Email;

/// <summary>
/// Rendered email content (subject + HTML body).
/// </summary>
public record EmailContent(string Subject, string HtmlBody);

/// <summary>
/// Renders email subject and body HTML for all system email types.
/// Separated from IEmailService (SMTP transport) to enable reuse by admin previews
/// and to centralize localization of email body text.
/// </summary>
public interface IEmailRenderer
{
    /// <summary>
    /// Application submitted notification (to admin, always English).
    /// </summary>
    EmailContent RenderApplicationSubmitted(Guid applicationId, string applicantName);

    /// <summary>
    /// Application approved notification.
    /// </summary>
    EmailContent RenderApplicationApproved(string userName, MembershipTier tier, string? culture = null);

    /// <summary>
    /// Application rejected notification.
    /// </summary>
    EmailContent RenderApplicationRejected(string userName, MembershipTier tier, string reason, string? culture = null);

    /// <summary>
    /// Signup rejected notification.
    /// </summary>
    EmailContent RenderSignupRejected(string userName, string? reason, string? culture = null);

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
    /// Added to team notification.
    /// </summary>
    EmailContent RenderAddedToTeam(string userName, string teamName, string teamSlug, IReadOnlyList<(string Name, string? Url)> resources, string? culture = null);

    /// <summary>
    /// Term renewal reminder for Colaborador/Asociado.
    /// </summary>
    EmailContent RenderTermRenewalReminder(string userName, string tierName, string expiresAt, string? culture = null);

    /// <summary>
    /// Feedback response notification.
    /// </summary>
    EmailContent RenderFeedbackResponse(string userName, string originalDescription, string responseMessage, string reportLink, string? culture = null);

    /// <summary>
    /// Issue comment notification — sent to the issue reporter when a non-reporter
    /// (admin/coordinator/board) posts a comment on their issue.
    /// </summary>
    EmailContent RenderIssueComment(string displayName, string issueTitle, string commentContent, string issueLink, string? culture = null);

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
    /// Renders a campaign-code email by substituting <c>{{Code}}</c> and
    /// <c>{{Name}}</c> placeholders in the campaign's markdown body and
    /// subject line, HTML-encoding the substituted values to prevent
    /// injection, and converting the resulting markdown body to HTML.
    /// </summary>
    EmailContent RenderCampaignCode(string subject, string markdownBody, string code, string recipientName);

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
}
