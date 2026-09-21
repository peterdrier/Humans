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
    /// Welcome email for new humans.
    /// </summary>
    EmailContent RenderWelcome(string userName, string? culture = null);

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

    /// <summary>
    /// Working-group register notice — dispatches on <see cref="WorkgroupNoticeRequest.Kind"/>
    /// to render the matching template. Builds the working-group link from
    /// <see cref="WorkgroupNoticeRequest.WorkgroupSlug"/> the same way a team link is
    /// built from a team slug.
    /// </summary>
    EmailContent RenderWorkgroupNotice(WorkgroupNoticeRequest request);

}
