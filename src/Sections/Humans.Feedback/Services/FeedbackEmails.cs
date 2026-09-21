using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Feedback.Services;

/// <summary>
/// Feedback's own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Feedback chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Feedback's own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class FeedbackEmails(
    IStringLocalizer<FeedbackResource> localizer,
    ILogger<FeedbackEmails> logger)
{
    public EmailMessage FeedbackResponse(string userEmail, string userName, string originalDescription, string responseMessage, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Feedback_Email_FeedbackResponse_Subject"),
            Lf("Feedback_Email_FeedbackResponse_Body", Encode(userName), Encode(originalDescription),
                SanitizedMarkdownRenderer.Render(responseMessage)),
            "feedback_response", MessageCategory.System));

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build();
        }
    }
}
