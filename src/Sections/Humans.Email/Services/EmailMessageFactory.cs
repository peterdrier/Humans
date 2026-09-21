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
