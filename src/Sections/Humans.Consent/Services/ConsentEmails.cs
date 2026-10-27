using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Consent.Services;

/// <summary>
/// Both templates are always-send (no <c>Category</c>): a member who hasn't signed a required document loses
/// access, so the notice isn't something to opt out of.
/// </summary>
internal sealed class ConsentEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<ConsentResource> localizer,
    ILogger<ConsentEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage ReConsentsRequired(Guid userId, string userEmail, string userName, IReadOnlyList<string> documentNames, string? culture = null)
        => Localized(culture, () =>
        {
            ArgumentNullException.ThrowIfNull(documentNames);
            var subject = documentNames.Count == 1
                ? Lf("Consent_Email_ReConsentRequired_Subject_Single", documentNames[0])
                : L("Consent_Email_ReConsentRequired_Subject_Multiple");
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li><strong>{Encode(d)}</strong></li>"));
            return new EmailMessage(userEmail, userName, userId,
                subject,
                Lf("Consent_Email_ReConsentsRequired_Body", Encode(userName), docsHtml, _settings.BaseUrl),
                "reconsents_required");
        });

    public EmailMessage ReConsentReminder(Guid userId, string userEmail, string userName, IReadOnlyList<string> documentNames, int daysRemaining, string? culture = null)
        => Localized(culture, () =>
        {
            ArgumentNullException.ThrowIfNull(documentNames);
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li>{Encode(d)}</li>"));
            return new EmailMessage(userEmail, userName, userId,
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
