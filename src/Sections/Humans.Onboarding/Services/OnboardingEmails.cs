using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Onboarding.Services;

/// <summary>
/// Onboarding's own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Onboarding chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Onboarding's own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class OnboardingEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<OnboardingResource> localizer,
    ILogger<OnboardingEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage SignupRejected(string userEmail, string userName, string? reason, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Onboarding_Email_SignupRejected_Subject"),
            Lf("Onboarding_Email_SignupRejected_Body", Encode(userName), ReasonLine(reason), _settings.AdminAddress),
            "signup_rejected", MessageCategory.System));

    /// <summary>Optional rejection reason — omitted entirely rather than rendered empty.</summary>
    private string ReasonLine(string? reason) =>
        string.IsNullOrEmpty(reason) ? "" : Lf("Onboarding_Email_ReasonLine", Encode(reason));

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
