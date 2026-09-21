using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Consent.Services;

/// <summary>
/// Consent's own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Consent chooses — template
/// name and opt-out category) for the single <see cref="IEmailService.SendAsync"/> path.
/// Copy lives in Consent's own resx set and is rendered in the recipient's culture inside
/// a <see cref="CultureScope"/> (memory/architecture/email-templates-live-in-sender.md,
/// peterdrier/Humans#1651). Pure — no I/O, no persistence.
/// </summary>
/// <remarks>
/// Both templates are always-send (no <c>Category</c>): a member who has not signed a
/// required document loses access, so the notice is not marketing they may opt out of.
/// </remarks>
internal sealed class ConsentEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<ConsentResource> localizer,
    ILogger<ConsentEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage ReConsentsRequired(string userEmail, string userName, IReadOnlyList<string> documentNames, string? culture = null)
        => Localized(culture, () =>
        {
            ArgumentNullException.ThrowIfNull(documentNames);
            var subject = documentNames.Count == 1
                ? Lf("Consent_Email_ReConsentRequired_Subject_Single", documentNames[0])
                : L("Consent_Email_ReConsentRequired_Subject_Multiple");
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li><strong>{Encode(d)}</strong></li>"));
            return new EmailMessage(userEmail, userName,
                subject,
                Lf("Consent_Email_ReConsentsRequired_Body", Encode(userName), docsHtml, _settings.BaseUrl),
                "reconsents_required");
        });

    public EmailMessage ReConsentReminder(string userEmail, string userName, IReadOnlyList<string> documentNames, int daysRemaining, string? culture = null)
        => Localized(culture, () =>
        {
            ArgumentNullException.ThrowIfNull(documentNames);
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li>{Encode(d)}</li>"));
            return new EmailMessage(userEmail, userName,
                Lf("Consent_Email_ReConsentReminder_Subject", daysRemaining),
                Lf("Consent_Email_ReConsentReminder_Body", Encode(userName), daysRemaining, docsHtml, _settings.BaseUrl),
                "reconsent_reminder");
        });

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
