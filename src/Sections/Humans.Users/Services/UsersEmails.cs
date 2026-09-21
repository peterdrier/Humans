using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Users.Services;

/// <summary>
/// Users' own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Users chooses —
/// template name, opt-out category and the do-not-persist rule for erasure mail) for
/// the single <see cref="IEmailService.SendAsync"/> path. Copy lives in Users' own resx
/// set and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class UsersEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<UsersResource> localizer,
    ILogger<UsersEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    /// <summary>
    /// Address-verification link. Always-send (no opt-out category) and time-sensitive:
    /// <see cref="TimeSensitiveTemplates.EmailVerification"/> is what makes the outbox
    /// drain it immediately, so the template name is load-bearing.
    /// </summary>
    public EmailMessage EmailVerification(string toEmail, string userName, string verificationUrl, bool isConflict = false, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, userName,
            L("Users_Email_VerifyEmail_Subject"),
            Lf(isConflict ? "Users_Email_EmailVerification_Merge_Body" : "Users_Email_EmailVerification_Body",
                Encode(userName), Encode(toEmail), verificationUrl),
            TimeSensitiveTemplates.EmailVerification));

    public EmailMessage AccountDeletionRequested(string userEmail, string userName, Instant deletionDate, string? culture = null)
    {
        // Invariant long date, as the factory formatted it — deliberately not culture-sensitive.
        var formattedDate = deletionDate.InUtc().Date.ToInvariantLongDate();
        return Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Users_Email_DeletionRequested_Subject"),
            Lf("Users_Email_AccountDeletionRequested_Body", Encode(userName), formattedDate, _settings.BaseUrl),
            "deletion_requested"));
    }

    /// <summary>
    /// Erasure confirmation. <c>DoNotPersist</c>: the recipient has just been erased, so an
    /// outbox row would re-create their address and name after Article 17 removed them.
    /// </summary>
    public EmailMessage AccountDeleted(string userEmail, string userName, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Users_Email_AccountDeleted_Subject"),
            Lf("Users_Email_AccountDeleted_Body", Encode(userName)),
            "account_deleted", DoNotPersist: true));

    public EmailMessage AccessSuspended(string userEmail, string userName, string reason, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Users_Email_AccessSuspended_Subject"),
            Lf("Users_Email_AccessSuspended_Body", Encode(userName), Encode(reason), _settings.BaseUrl, _settings.AdminAddress),
            "access_suspended"));

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
