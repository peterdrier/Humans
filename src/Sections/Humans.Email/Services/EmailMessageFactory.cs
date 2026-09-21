using Humans.Users.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Default <see cref="IEmailMessageFactory"/>: renders each system email through the
/// pure <see cref="IEmailRenderer"/> and stamps the routing policy that the
/// <see cref="IEmailService"/> transport reads (template name, opt-out category,
/// reply-to, immediate-drain, campaign user/grant ids). Pure mapping — no I/O.
/// </summary>
internal sealed class EmailMessageFactory(IEmailRenderer renderer) : IEmailMessageFactory
{
    public EmailMessage FacilitatedMessage(string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null)
    {
        var content = renderer.RenderFacilitatedMessage(recipientName, senderName, messageText, includeContactInfo, senderEmail, culture);
        var replyTo = includeContactInfo ? senderEmail : null;
        return new EmailMessage(recipientEmail, recipientName, content.Subject, content.HtmlBody,
            "facilitated_message", MessageCategory.FacilitatedMessages, ReplyTo: replyTo);
    }

    public EmailMessage WorkspaceCredentials(string recoveryEmail, string userName, string workspaceEmail, string tempPassword, string? culture = null)
    {
        var content = renderer.RenderWorkspaceCredentials(userName, workspaceEmail, tempPassword, culture);
        return new EmailMessage(recoveryEmail, userName, content.Subject, content.HtmlBody,
            TimeSensitiveTemplates.WorkspaceCredentials);
    }

    public EmailMessage GoogleGroupRemovalLossOfAccess(string removedEmail, string userName, string groupName, string groupEmail, string? culture = null)
    {
        var content = renderer.RenderGoogleGroupRemovalLossOfAccess(userName, groupName, groupEmail, culture);
        return new EmailMessage(removedEmail, userName, content.Subject, content.HtmlBody,
            "google_group_removal_loss_of_access", MessageCategory.System);
    }

    public EmailMessage GoogleDriveRemovalLossOfAccess(string removedEmail, string userName, string folderName, string? culture = null)
    {
        var content = renderer.RenderGoogleDriveRemovalLossOfAccess(userName, folderName, culture);
        return new EmailMessage(removedEmail, userName, content.Subject, content.HtmlBody,
            "google_drive_removal_loss_of_access", MessageCategory.System);
    }

    public EmailMessage GoogleAccessRemovalSecondaryCleanup(string removedEmail, string userName, string currentGoogleEmail, string? culture = null)
    {
        var content = renderer.RenderGoogleAccessRemovalSecondaryCleanup(userName, removedEmail, currentGoogleEmail, culture);
        return new EmailMessage(removedEmail, userName, content.Subject, content.HtmlBody,
            "google_access_removal_secondary_cleanup", MessageCategory.System);
    }

    public EmailMessage WorkgroupNotice(WorkgroupNoticeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = renderer.RenderWorkgroupNotice(request);
        var templateName = request.Kind switch
        {
            WorkgroupNoticeKind.Applied => "workgroup_notice_applied",
            WorkgroupNoticeKind.Referred => "workgroup_notice_referred",
            WorkgroupNoticeKind.Registered => "workgroup_notice_registered",
            WorkgroupNoticeKind.Refused => "workgroup_notice_refused",
            WorkgroupNoticeKind.Withdrawn => "workgroup_notice_withdrawn",
            WorkgroupNoticeKind.Ended => "workgroup_notice_ended",
            WorkgroupNoticeKind.Reactivated => "workgroup_notice_reactivated",
            WorkgroupNoticeKind.CoordinatorsChanged => "workgroup_notice_coordinators_changed",
            WorkgroupNoticeKind.DormancyInquiry => "workgroup_notice_dormancy_inquiry",
            WorkgroupNoticeKind.Delivered => "workgroup_notice_delivered",
            WorkgroupNoticeKind.DispositionRecorded => "workgroup_notice_disposition_recorded",
            _ => throw new InvalidOperationException($"WorkgroupNotice does not support kind {request.Kind}")
        };
        return new EmailMessage(request.RecipientEmail, request.RecipientName, content.Subject, content.HtmlBody,
            templateName, MessageCategory.Governance);
    }

}
