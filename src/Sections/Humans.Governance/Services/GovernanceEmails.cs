using System.Globalization;
using System.Net;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Governance's own email templates: one method per template, each returning a ready
/// <see cref="EmailMessage"/> (content plus the routing policy Governance chooses —
/// template name and opt-out category) for the single
/// <see cref="IEmailService.SendAsync"/> path. Copy lives in Governance's own resx set
/// and is rendered in the recipient's culture inside a <see cref="CultureScope"/>
/// (memory/architecture/email-templates-live-in-sender.md, peterdrier/Humans#1651).
/// Pure — no I/O, no persistence.
/// </summary>
internal sealed class GovernanceEmails(
    IOptions<EmailSettings> settings,
    IStringLocalizer<GovernanceResource> localizer,
    ILogger<GovernanceEmails> logger)
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailMessage ApplicationApproved(string userEmail, string userName, MembershipTier tier, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Governance_Email_ApplicationApproved_Subject"),
            Lf("Governance_Email_ApplicationApproved_Body", Encode(userName), tier, _settings.BaseUrl),
            "application_approved", MessageCategory.Governance));

    public EmailMessage ApplicationRejected(string userEmail, string userName, MembershipTier tier, string reason, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            L("Governance_Email_ApplicationRejected_Subject"),
            Lf("Governance_Email_ApplicationRejected_Body", Encode(userName), tier, ReasonLine(reason), _settings.AdminAddress),
            "application_rejected", MessageCategory.Governance));

    public EmailMessage TermRenewalReminder(string userEmail, string userName, string tierName, string expiresAt, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            userEmail, userName,
            Lf("Governance_Email_TermRenewalReminder_Subject", tierName),
            Lf("Governance_Email_TermRenewalReminder_Body", Encode(userName), Encode(tierName), Encode(expiresAt), _settings.BaseUrl),
            "term_renewal_reminder", MessageCategory.Governance));

    public EmailMessage AssemblyVoteOpened(string toEmail, string userName, string voteTitle, LocalDateTime closesAt, bool isOfficial, string voteUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, userName,
            Lf("Governance_Email_AssemblyVoteOpened_Subject", voteTitle),
            Lf("Governance_Email_AssemblyVoteOpened_Body", Encode(userName), Encode(voteTitle),
                Encode(closesAt.ToDateTime()), AbsoluteUrl(voteUrl), IndicativeNote(isOfficial)),
            "assembly_vote_opened", MessageCategory.System));

    public EmailMessage AssemblyVoteReminder(string toEmail, string userName, string voteTitle, LocalDateTime closesAt, bool isOfficial, string voteUrl, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, userName,
            Lf("Governance_Email_AssemblyVoteReminder_Subject", voteTitle),
            Lf("Governance_Email_AssemblyVoteReminder_Body", Encode(userName), Encode(voteTitle),
                Encode(closesAt.ToDateTime()), AbsoluteUrl(voteUrl), IndicativeNote(isOfficial)),
            "assembly_vote_reminder", MessageCategory.System));

    public EmailMessage AssemblyVoteCancelled(string toEmail, string userName, string voteTitle, string reason, string? culture = null)
        => Localized(culture, () => new EmailMessage(
            toEmail, userName,
            Lf("Governance_Email_AssemblyVoteCancelled_Subject", voteTitle),
            Lf("Governance_Email_AssemblyVoteCancelled_Body", Encode(userName), Encode(voteTitle), Lf("Governance_Email_ReasonLine", Encode(reason))),
            "assembly_vote_cancelled", MessageCategory.System));

    /// <summary>Optional rejection reason — omitted entirely rather than rendered empty.</summary>
    private string ReasonLine(string? reason) =>
        string.IsNullOrEmpty(reason) ? "" : Lf("Governance_Email_ReasonLine", Encode(reason));

    /// <summary>The "your ballot is not counted" caveat carried only by indicative votes.</summary>
    private string IndicativeNote(bool isOfficial) =>
        isOfficial ? "" : L("Governance_Email_AssemblyVote_IndicativeNote");

    /// <summary>
    /// Webmail has no Humans origin, so a root-relative path in an email resolves against
    /// the reader's own host; callers pass the in-app path and the absolute link is built here.
    /// </summary>
    private string AbsoluteUrl(string path) =>
        $"{_settings.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

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
