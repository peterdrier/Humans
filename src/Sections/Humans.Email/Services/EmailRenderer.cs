using Humans.Users.Contracts;
using System.Globalization;
using Humans.Email.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Email.Services;

/// <summary>
/// Renders email subject + body HTML for all system email types.
/// Body text is localized via the section's own EmailResource resx set.
/// </summary>
internal sealed class EmailRenderer(
    IOptions<EmailSettings> settings,
    IStringLocalizer<EmailResource> localizer,
    ILogger<EmailRenderer> logger) : IEmailRenderer
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailContent RenderApplicationSubmitted(Guid applicationId, string applicantName)
    {
        // Admin email — always English, no culture switch
        return new EmailContent(
            Lf("Email_ApplicationSubmitted_Subject", applicantName),
            Lf("Email_ApplicationSubmitted_Body", HtmlEncode(applicantName), applicationId, _settings.BaseUrl));
    }

    public EmailContent RenderWelcome(string userName, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_Welcome_Subject"),
            Lf("Email_Welcome_Body", HtmlEncode(userName), _settings.BaseUrl)));

    public EmailContent RenderFacilitatedMessage(
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(messageText);

            var contactInfoHtml = includeContactInfo && !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{HtmlEncode(senderName)}</strong> &mdash; <a href=\"mailto:{HtmlEncode(senderEmail)}\">{HtmlEncode(senderEmail)}</a></p>"
                : $"<p><em>{HtmlEncode(L("Email_FacilitatedMessage_NoContactInfo"))}</em></p>";

            return new EmailContent(
                Lf("Email_FacilitatedMessage_Subject", senderName),
                Lf("Email_FacilitatedMessage_Body", HtmlEncode(recipientName), HtmlEncode(senderName), sanitizedMessage, contactInfoHtml));
        });

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private EmailContent RenderLocalized(string? culture, Func<EmailContent> render)
    {
        using (new CultureScope(culture, logger))
        {
            return render();
        }
    }

    private static string HtmlEncode(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text);
    }

    /// <summary>
    /// Webmail has no Humans origin, so a root-relative path in an email resolves against the
    /// reader's own host. Callers pass the in-app path; the absolute link is built here, the
    /// same way survey and team links are.
    /// </summary>
    private string AbsoluteUrl(string path) =>
        $"{_settings.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

}
