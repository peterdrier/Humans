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

}
