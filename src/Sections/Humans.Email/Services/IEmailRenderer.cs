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
    /// Working-group register notice — dispatches on <see cref="WorkgroupNoticeRequest.Kind"/>
    /// to render the matching template. Builds the working-group link from
    /// <see cref="WorkgroupNoticeRequest.WorkgroupSlug"/> the same way a team link is
    /// built from a team slug.
    /// </summary>
    EmailContent RenderWorkgroupNotice(WorkgroupNoticeRequest request);

}
