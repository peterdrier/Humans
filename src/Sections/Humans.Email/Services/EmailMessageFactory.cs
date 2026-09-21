using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Services;

/// <summary>
/// Builds Email's one remaining template, the facilitated volunteer-to-volunteer relay:
/// content plus the routing policy the <see cref="IEmailService"/> transport reads
/// (template name, opt-out category, reply-to). Copy lives in Email's own resx set and
/// renders in the recipient's culture inside a <see cref="CultureScope"/>. Every other
/// template is built by its sending section's own <c>&lt;Section&gt;Emails</c>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O.
/// </summary>
internal sealed class EmailMessageFactory(
    IStringLocalizer<EmailResource> localizer,
    ILogger<EmailMessageFactory> logger) : IEmailMessageFactory
{
    public EmailMessage FacilitatedMessage(string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(messageText);

            var contactInfoHtml = includeContactInfo && !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{Encode(senderName)}</strong> &mdash; <a href=\"mailto:{Encode(senderEmail)}\">{Encode(senderEmail)}</a></p>"
                : $"<p><em>{Encode(L("Email_FacilitatedMessage_NoContactInfo"))}</em></p>";

            return new EmailMessage(
                recipientEmail, recipientName,
                Lf("Email_FacilitatedMessage_Subject", senderName),
                Lf("Email_FacilitatedMessage_Body", Encode(recipientName), Encode(senderName), sanitizedMessage, contactInfoHtml),
                "facilitated_message", MessageCategory.FacilitatedMessages,
                ReplyTo: includeContactInfo ? senderEmail : null);
        }
    }

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
