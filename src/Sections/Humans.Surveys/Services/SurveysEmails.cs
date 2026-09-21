using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Surveys.Services;

/// <summary>
/// Surveys' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Surveys chooses — template
/// name and opt-out category) for the single <see cref="IEmailService.SendAsync"/> path.
/// Copy lives in Surveys' own resx set and is rendered in the recipient's culture inside a
/// <see cref="CultureScope"/> (memory/architecture/email-templates-live-in-sender.md,
/// peterdrier/Humans#1651). Pure — no I/O, no persistence.
/// </summary>
internal sealed class SurveysEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<SurveysResource> localizer,
    ILogger<SurveysEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>
    /// Survey invitation — operational (System category, always-send).
    /// <paramref name="answerToken"/> is the invite token; the URL is built here.
    /// Blank custom copy retains the standard localized wording.
    /// </summary>
    public EmailMessage SurveyInvitation(
        string userEmail,
        string userName,
        string surveyTitle,
        string answerToken,
        string? culture = null,
        string? customSubject = null,
        string? customMessage = null)
        => Localized(culture, () =>
        {
            var subject = string.IsNullOrWhiteSpace(customSubject)
                ? Lf("Surveys_Email_SurveyInvitation_Subject", surveyTitle)
                : customSubject.Trim();
            var messageHtml = string.IsNullOrWhiteSpace(customMessage)
                ? $"<p>{Lf("Surveys_Email_SurveyInvitation_DefaultMessage", Encode(surveyTitle))}</p>"
                : SanitizedMarkdownRenderer.Render(customMessage.Trim());

            return new EmailMessage(userEmail, userName,
                subject,
                Lf("Surveys_Email_SurveyInvitation_Body",
                    Encode(userName),
                    Encode(surveyTitle),
                    AnswerUrl(answerToken),
                    messageHtml),
                "survey_invitation", MessageCategory.System);
        });

    /// <summary>Survey reminder — single nudge for an unfinished invitation (System category).</summary>
    public EmailMessage SurveyReminder(string userEmail, string userName, string surveyTitle, string answerToken, string? culture = null)
        => Localized(culture, () => new EmailMessage(userEmail, userName,
            Lf("Surveys_Email_SurveyReminder_Subject", surveyTitle),
            Lf("Surveys_Email_SurveyReminder_Body", Encode(userName), Encode(surveyTitle), AnswerUrl(answerToken)),
            "survey_reminder", MessageCategory.System));

    /// <summary>
    /// Webmail has no Humans origin, so the answering wizard is linked absolutely; the token
    /// is escaped because it travels in the query string.
    /// </summary>
    private string AnswerUrl(string token)
        => $"{_settings.BaseUrl.TrimEnd('/')}/Survey/Answer?t={Uri.EscapeDataString(token)}";

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
