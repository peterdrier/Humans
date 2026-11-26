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
    // Template names are stored on outbox rows, so they keep their original names.
    internal const string SignInLinkTemplate = "magic_link_login";
    internal const string SignInLinkSignupTemplate = "magic_link_signup";

    public EmailMessage SignInLink(Guid userId, string toEmail, string displayName, string signInLinkUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, displayName, userId,
            L("Auth_Email_SignInLink_Subject"),
            localizer["Auth_Email_SignInLink_Body", Encode(displayName), signInLinkUrl],
            SignInLinkTemplate, Urgent: true));

    /// <summary>No account stands behind the address yet, so the message carries no user.</summary>
    public EmailMessage SignInLinkSignup(string toEmail, string signInLinkUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, toEmail, UserId: null,
            L("Auth_Email_SignInLinkSignup_Subject"),
            localizer["Auth_Email_SignInLinkSignup_Body", signInLinkUrl],
            SignInLinkSignupTemplate, Urgent: true));

    private string L(string key) => localizer[key].Value;

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private EmailMessage Localized(string? culture, Func<EmailMessage> build)
    {
        using (new CultureScope(culture, logger))
        {
            return build();
        }
    }
}
