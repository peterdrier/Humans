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

    public EmailContent RenderWorkgroupNotice(WorkgroupNoticeRequest request)
        => RenderLocalized(request.Culture, () =>
        {
            var greeting = string.IsNullOrEmpty(request.RecipientName)
                ? L("Email_WorkgroupNotice_Greeting_Generic")
                : Lf("Email_WorkgroupNotice_Greeting_Named", HtmlEncode(request.RecipientName));
            // Two forms of the same name: subjects are plain text, bodies are HTML. Encoding
            // once for both would put "Health &amp; Safety" in the subject line.
            var name = request.WorkgroupName;
            var nameHtml = HtmlEncode(request.WorkgroupName);
            var url = $"{_settings.BaseUrl}/Workgroups/{request.WorkgroupSlug}";
            var detail = HtmlEncode(request.Detail ?? "");

            return request.Kind switch
            {
                WorkgroupNoticeKind.Applied => new EmailContent(
                    Lf("Email_WorkgroupNotice_Applied_Subject", name),
                    Lf("Email_WorkgroupNotice_Applied_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Referred => new EmailContent(
                    Lf("Email_WorkgroupNotice_Referred_Subject", name),
                    Lf("Email_WorkgroupNotice_Referred_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Registered => new EmailContent(
                    Lf("Email_WorkgroupNotice_Registered_Subject", name),
                    Lf("Email_WorkgroupNotice_Registered_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Refused => new EmailContent(
                    Lf("Email_WorkgroupNotice_Refused_Subject", name),
                    Lf("Email_WorkgroupNotice_Refused_Body", greeting, nameHtml,
                        string.IsNullOrEmpty(request.Detail) ? "" : Lf("Email_ReasonLine", detail), url)),
                WorkgroupNoticeKind.Withdrawn => new EmailContent(
                    Lf("Email_WorkgroupNotice_Withdrawn_Subject", name),
                    Lf("Email_WorkgroupNotice_Withdrawn_Body", greeting, nameHtml,
                        string.IsNullOrEmpty(request.Detail) ? "" : Lf("Email_ReasonLine", detail), url)),
                WorkgroupNoticeKind.Ended => new EmailContent(
                    Lf("Email_WorkgroupNotice_Ended_Subject", name),
                    Lf("Email_WorkgroupNotice_Ended_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Reactivated => new EmailContent(
                    Lf("Email_WorkgroupNotice_Reactivated_Subject", name),
                    Lf("Email_WorkgroupNotice_Reactivated_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.CoordinatorsChanged => new EmailContent(
                    Lf("Email_WorkgroupNotice_CoordinatorsChanged_Subject", name),
                    Lf("Email_WorkgroupNotice_CoordinatorsChanged_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.DormancyInquiry => new EmailContent(
                    Lf("Email_WorkgroupNotice_DormancyInquiry_Subject", name),
                    Lf("Email_WorkgroupNotice_DormancyInquiry_Body", greeting, nameHtml, detail, url)),
                WorkgroupNoticeKind.Delivered => new EmailContent(
                    Lf("Email_WorkgroupNotice_Delivered_Subject", name),
                    Lf("Email_WorkgroupNotice_Delivered_Body", greeting, nameHtml, detail, url)),
                WorkgroupNoticeKind.DispositionRecorded => new EmailContent(
                    Lf("Email_WorkgroupNotice_DispositionRecorded_Subject", name),
                    Lf("Email_WorkgroupNotice_DispositionRecorded_Body", greeting, nameHtml, detail, url)),
                _ => throw new InvalidOperationException(
                    $"WorkgroupNotice does not support kind {request.Kind}")
            };
        });

    /// <summary>
    /// Webmail has no Humans origin, so a root-relative path in an email resolves against the
    /// reader's own host. Callers pass the in-app path; the absolute link is built here, the
    /// same way survey and team links are.
    /// </summary>
    private string AbsoluteUrl(string path) =>
        $"{_settings.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

}
