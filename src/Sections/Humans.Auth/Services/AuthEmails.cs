using System.Globalization;
using System.Net;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Auth.Services;

/// <summary>
/// Both templates are <see cref="EmailMessage.Urgent"/>: the outbox drains a sign-in link immediately
/// instead of on the next sweep.
/// </summary>
internal sealed class AuthEmails(
    IStringLocalizer<AuthResource> localizer,
    ILogger<AuthEmails> logger)
{
    internal const string MagicLinkLoginTemplate = "magic_link_login";
    internal const string MagicLinkSignupTemplate = "magic_link_signup";

    public EmailMessage MagicLinkLogin(Guid userId, string toEmail, string displayName, string magicLinkUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, displayName, userId,
            L("Auth_Email_MagicLinkLogin_Subject"),
            Lf("Auth_Email_MagicLinkLogin_Body", Encode(displayName), magicLinkUrl),
            MagicLinkLoginTemplate, Urgent: true));

    /// <summary>No account stands behind the address yet, so the message carries no user.</summary>
    public EmailMessage MagicLinkSignup(string toEmail, string magicLinkUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, toEmail, UserId: null,
            L("Auth_Email_MagicLinkSignup_Subject"),
            Lf("Auth_Email_MagicLinkSignup_Body", magicLinkUrl),
            MagicLinkSignupTemplate, Urgent: true));

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
