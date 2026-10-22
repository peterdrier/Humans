using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Services;

internal sealed class EmailMessageFactory(
    IStringLocalizer<EmailResource> localizer,
    ILogger<EmailMessageFactory> logger) : IEmailMessageFactory
{
    public EmailMessage FacilitatedMessage(Guid? recipientUserId, string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null, string? replyTo = null)
    {
        using (new CultureScope(culture, logger))
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(messageText);

            var contactInfoHtml = includeContactInfo && !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{Encode(senderName)}</strong> &mdash; <a href=\"mailto:{Encode(senderEmail)}\">{Encode(senderEmail)}</a></p>"
                : $"<p><em>{Encode(L("Email_FacilitatedMessage_NoContactInfo"))}</em></p>";

            return new EmailMessage(
                recipientEmail, recipientName, recipientUserId,
                Lf("Email_FacilitatedMessage_Subject", senderName),
                Lf("Email_FacilitatedMessage_Body", Encode(recipientName), Encode(senderName), sanitizedMessage, contactInfoHtml),
                "facilitated_message", MessageCategory.FacilitatedMessages,
                ReplyTo: replyTo ?? (includeContactInfo ? senderEmail : null), Culture: CultureInfo.CurrentUICulture.Name);
        }
    }

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
