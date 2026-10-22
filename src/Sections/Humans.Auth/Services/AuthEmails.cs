using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Auth.Services;

/// <summary>
/// Both templates are <see cref="TimeSensitiveTemplates"/>: the outbox drains a sign-in link immediately
/// instead of on the next sweep.
/// </summary>
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
