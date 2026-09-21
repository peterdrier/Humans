using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Humans.Auth.Services;

/// <summary>
/// Auth's own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Auth chooses — template
/// name and opt-out category) for the single <see cref="IEmailService.SendAsync"/> path.
/// Copy lives in Auth's own resx set and is rendered in the recipient's culture inside a
/// <see cref="CultureScope"/> (memory/architecture/email-templates-live-in-sender.md,
/// peterdrier/Humans#1651). Pure — no I/O, no persistence.
/// </summary>
/// <remarks>
/// Both templates keep their <see cref="TimeSensitiveTemplates"/> names: the outbox reads
/// those to drain a sign-in link immediately instead of on the next sweep.
/// </remarks>
internal sealed class AuthEmails(
    IStringLocalizer<AuthResource> localizer,
    ILogger<AuthEmails> logger)
{
    public EmailMessage MagicLinkLogin(string toEmail, string displayName, string magicLinkUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, displayName,
            L("Auth_Email_MagicLinkLogin_Subject"),
            Lf("Auth_Email_MagicLinkLogin_Body", Encode(displayName), magicLinkUrl),
            TimeSensitiveTemplates.MagicLinkLogin));

    public EmailMessage MagicLinkSignup(string toEmail, string magicLinkUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, toEmail,
            L("Auth_Email_MagicLinkSignup_Subject"),
            Lf("Auth_Email_MagicLinkSignup_Body", magicLinkUrl),
            TimeSensitiveTemplates.MagicLinkSignup));

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
