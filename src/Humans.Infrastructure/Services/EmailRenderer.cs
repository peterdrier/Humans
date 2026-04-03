using System.Globalization;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Renders email subject + body HTML for all system email types.
/// Body text is localized via SharedResource resx keys.
/// </summary>
public class EmailRenderer : IEmailRenderer
{
    private readonly EmailSettings _settings;
    private readonly IStringLocalizer _localizer;
    private readonly ILogger<EmailRenderer> _logger;

    public EmailRenderer(
        IOptions<EmailSettings> settings,
        IStringLocalizerFactory localizerFactory,
        ILogger<EmailRenderer> logger)
    {
        _settings = settings.Value;
        _localizer = localizerFactory.Create("SharedResource", "Humans.Web");
        _logger = logger;
    }

    public EmailContent RenderApplicationSubmitted(Guid applicationId, string applicantName)
    {
        // Admin email — always English, no culture switch
        return new EmailContent(
            Lf("Email_ApplicationSubmitted_Subject", applicantName),
            Lf("Email_ApplicationSubmitted_Body", HtmlEncode(applicantName), applicationId, _settings.BaseUrl));
    }

    public EmailContent RenderApplicationApproved(string userName, MembershipTier tier, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_ApplicationApproved_Subject"),
            Lf("Email_ApplicationApproved_Body", HtmlEncode(userName), tier, _settings.BaseUrl)));

    public EmailContent RenderApplicationRejected(string userName, MembershipTier tier, string reason, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var reasonHtml = string.IsNullOrEmpty(reason)
                ? ""
                : Lf("Email_ReasonLine", HtmlEncode(reason));
            return new EmailContent(
                L("Email_ApplicationRejected_Subject"),
                Lf("Email_ApplicationRejected_Body", HtmlEncode(userName), tier, reasonHtml, _settings.AdminAddress));
        });

    public EmailContent RenderSignupRejected(string userName, string? reason, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var reasonHtml = string.IsNullOrEmpty(reason)
                ? ""
                : Lf("Email_ReasonLine", HtmlEncode(reason));
            return new EmailContent(
                L("Email_SignupRejected_Subject"),
                Lf("Email_SignupRejected_Body", HtmlEncode(userName), reasonHtml, _settings.AdminAddress));
        });

    public EmailContent RenderReConsentsRequired(string userName, IReadOnlyList<string> documentNames, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var subject = documentNames.Count == 1
                ? Lf("Email_ReConsentRequired_Subject_Single", documentNames[0])
                : L("Email_ReConsentRequired_Subject_Multiple");
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li><strong>{HtmlEncode(d)}</strong></li>"));
            return new EmailContent(
                subject,
                Lf("Email_ReConsentsRequired_Body", HtmlEncode(userName), docsHtml, _settings.BaseUrl));
        });

    public EmailContent RenderReConsentReminder(string userName, IReadOnlyList<string> documentNames, int daysRemaining, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li>{HtmlEncode(d)}</li>"));
            return new EmailContent(
                Lf("Email_ReConsentReminder_Subject", daysRemaining),
                Lf("Email_ReConsentReminder_Body", HtmlEncode(userName), daysRemaining, docsHtml, _settings.BaseUrl));
        });

    public EmailContent RenderWelcome(string userName, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_Welcome_Subject"),
            Lf("Email_Welcome_Body", HtmlEncode(userName), _settings.BaseUrl)));

    public EmailContent RenderAccessSuspended(string userName, string reason, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_AccessSuspended_Subject"),
            Lf("Email_AccessSuspended_Body", HtmlEncode(userName), HtmlEncode(reason), _settings.BaseUrl, _settings.AdminAddress)));

    public EmailContent RenderEmailVerification(string userName, string toEmail, string verificationUrl, bool isConflict = false, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var templateKey = isConflict
                ? "Email_EmailVerification_Merge_Body"
                : "Email_EmailVerification_Body";
            return new EmailContent(
                L("Email_VerifyEmail_Subject"),
                Lf(templateKey, HtmlEncode(userName), HtmlEncode(toEmail), verificationUrl));
        });

    public EmailContent RenderAccountDeletionRequested(string userName, string formattedDeletionDate, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_DeletionRequested_Subject"),
            Lf("Email_AccountDeletionRequested_Body", HtmlEncode(userName), formattedDeletionDate, _settings.BaseUrl)));

    public EmailContent RenderAccountDeleted(string userName, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_AccountDeleted_Subject"),
            Lf("Email_AccountDeleted_Body", HtmlEncode(userName))));

    public EmailContent RenderAddedToTeam(string userName, string teamName, string teamSlug, IReadOnlyList<(string Name, string? Url)> resources, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var teamUrl = $"{_settings.BaseUrl}/Teams/{teamSlug}";
            var resourcesHtml = resources.Count > 0
                ? Lf("Email_ResourcesSection",
                    string.Join("\n", resources.Select(r =>
                        !string.IsNullOrEmpty(r.Url)
                            ? $"<li><a href=\"{r.Url}\">{HtmlEncode(r.Name)}</a></li>"
                            : $"<li>{HtmlEncode(r.Name)}</li>")))
                : "";
            return new EmailContent(
                Lf("Email_AddedToTeam_Subject", teamName),
                Lf("Email_AddedToTeam_Body", HtmlEncode(userName), HtmlEncode(teamName), resourcesHtml, teamUrl));
        });

    public EmailContent RenderTermRenewalReminder(string userName, string tierName, string expiresAt, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            Lf("Email_TermRenewalReminder_Subject", tierName),
            Lf("Email_TermRenewalReminder_Body", HtmlEncode(userName), HtmlEncode(tierName), HtmlEncode(expiresAt), _settings.BaseUrl)));

    public EmailContent RenderBoardDailyDigest(string boardMemberName, string date, IReadOnlyList<BoardDigestTierGroup> tierGroups, BoardDigestOutstandingCounts? outstandingCounts = null, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var outstandingHtml = BuildOutstandingSection(outstandingCounts);

            var approvalsHtml = tierGroups.Count > 0
                ? string.Join("\n", tierGroups.Select(g =>
                    $"<h3>{HtmlEncode(g.TierLabel)}</h3>\n<ul>\n{string.Join("\n", g.DisplayNames.Select(n => $"<li>{HtmlEncode(n)}</li>"))}\n</ul>"))
                : $"<p>{L("Email_BoardDigest_NoApprovals")}</p>";

            return new EmailContent(
                Lf("Email_BoardDailyDigest_Subject", date),
                Lf("Email_BoardDailyDigest_Body", HtmlEncode(boardMemberName), HtmlEncode(date), approvalsHtml, outstandingHtml));
        });

    private string BuildOutstandingSection(BoardDigestOutstandingCounts? counts)
    {
        if (counts is null) return "";

        var hasAny = counts.OnboardingReview > 0 || counts.StillOnboarding > 0
            || counts.BoardVotingTotal > 0 || counts.TeamJoinRequests > 0
            || counts.PendingConsents > 0 || counts.PendingDeletions > 0;
        if (!hasAny) return "";

        var items = new List<string>();

        if (counts.OnboardingReview > 0)
            items.Add($"<li>{Lf("Email_BoardDigest_OnboardingReview", counts.OnboardingReview)} <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.StillOnboarding > 0)
            items.Add($"<li>{Lf("Email_BoardDigest_StillOnboarding", counts.StillOnboarding)} <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.BoardVotingTotal > 0)
            items.Add($"<li>{Lf("Email_BoardDigest_BoardVoting", counts.BoardVotingTotal, counts.BoardVotingYours)} <a href=\"{_settings.BaseUrl}/OnboardingReview/BoardVoting\">&rarr;</a></li>");

        if (counts.TeamJoinRequests > 0)
            items.Add($"<li>{Lf("Email_BoardDigest_TeamJoinRequests", counts.TeamJoinRequests)} <a href=\"{_settings.BaseUrl}/Teams/Summary\">&rarr;</a></li>");

        if (counts.PendingConsents > 0)
            items.Add($"<li>{Lf("Email_BoardDigest_PendingConsents", counts.PendingConsents)} <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.PendingDeletions > 0)
            items.Add($"<li>{Lf("Email_BoardDigest_PendingDeletions", counts.PendingDeletions)} <a href=\"{_settings.BaseUrl}/Admin\">&rarr;</a></li>");

        var header = L("Email_BoardDigest_OutstandingHeader");
        return $"<h3>{HtmlEncode(header)}</h3>\n<ul>\n{string.Join("\n", items)}\n</ul>\n<hr/>";
    }

    public EmailContent RenderAdminDailyDigest(string adminName, string date, AdminDigestCounts counts, string? culture = null)
    {
        // Admin digest is always English (admin pages aren't localized)
        var subject = $"Admin Digest: {date}";

        var items = new List<string>();

        if (counts.PendingDeletions > 0)
            items.Add($"<li><strong>{counts.PendingDeletions}</strong> account deletions pending <a href=\"{_settings.BaseUrl}/Admin\">&rarr;</a></li>");

        if (counts.PendingConsents > 0)
            items.Add($"<li><strong>{counts.PendingConsents}</strong> with outstanding consent requirements <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.TeamJoinRequests > 0)
            items.Add($"<li><strong>{counts.TeamJoinRequests}</strong> team join requests pending <a href=\"{_settings.BaseUrl}/Teams/Summary\">&rarr;</a></li>");

        if (counts.OnboardingReview > 0)
            items.Add($"<li><strong>{counts.OnboardingReview}</strong> awaiting onboarding review <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.StillOnboarding > 0)
            items.Add($"<li><strong>{counts.StillOnboarding}</strong> still completing onboarding <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.BoardVotingTotal > 0)
            items.Add($"<li><strong>{counts.BoardVotingTotal}</strong> tier applications awaiting vote <a href=\"{_settings.BaseUrl}/OnboardingReview/BoardVoting\">&rarr;</a></li>");

        if (counts.FailedSyncOutboxEvents > 0)
            items.Add($"<li><strong>{counts.FailedSyncOutboxEvents}</strong> failed Google sync outbox events (transient, retrying) <a href=\"{_settings.BaseUrl}/Google/Sync\">&rarr;</a></li>");

        if (counts.PermanentSyncFailures > 0)
            items.Add($"<li><strong>{counts.PermanentSyncFailures}</strong> humans with rejected Google email (sync blocked) <a href=\"{_settings.BaseUrl}/Google/Sync\">&rarr;</a></li>");

        if (counts.TransientSyncRetries > 0)
            items.Add($"<li><strong>{counts.TransientSyncRetries}</strong> Google sync events retrying (transient errors) <a href=\"{_settings.BaseUrl}/Google/Sync\">&rarr;</a></li>");

        if (counts.TicketSyncError)
            items.Add($"<li>Ticket sync error: {HtmlEncode(counts.TicketSyncErrorMessage ?? "Unknown")} <a href=\"{_settings.BaseUrl}/Tickets\">&rarr;</a></li>");

        var itemsHtml = items.Count > 0
            ? $"<ul>\n{string.Join("\n", items)}\n</ul>"
            : "<p>All clear — no pending items.</p>";

        var body = $"<h2>Admin Digest — {HtmlEncode(date)}</h2>\n<p>Hi {HtmlEncode(adminName)},</p>\n<h3>Pending Actions &amp; System Health</h3>\n{itemsHtml}";
        return new EmailContent(subject, body);
    }

    public EmailContent RenderFeedbackResponse(string userName, string originalDescription, string responseMessage, string reportLink, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var responseHtml = Markdig.Markdown.ToHtml(responseMessage);
            return new EmailContent(
                L("Email_FeedbackResponse_Subject"),
                Lf("Email_FeedbackResponse_Body", HtmlEncode(userName), HtmlEncode(originalDescription), responseHtml, HtmlEncode(reportLink)));
        });

    public EmailContent RenderFacilitatedMessage(
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var sanitizedMessage = HtmlEncode(messageText).Replace("\n", "<br />", StringComparison.Ordinal);

            var contactInfoHtml = includeContactInfo && !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{HtmlEncode(senderName)}</strong> &mdash; <a href=\"mailto:{HtmlEncode(senderEmail)}\">{HtmlEncode(senderEmail)}</a></p>"
                : $"<p><em>{HtmlEncode(L("Email_FacilitatedMessage_NoContactInfo"))}</em></p>";

            return new EmailContent(
                Lf("Email_FacilitatedMessage_Subject", senderName),
                Lf("Email_FacilitatedMessage_Body", HtmlEncode(recipientName), HtmlEncode(senderName), sanitizedMessage, contactInfoHtml));
        });

    public EmailContent RenderMagicLinkLogin(string displayName, string magicLinkUrl, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_MagicLinkLogin_Subject"),
            Lf("Email_MagicLinkLogin_Body", HtmlEncode(displayName), magicLinkUrl)));

    public EmailContent RenderMagicLinkSignup(string magicLinkUrl, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_MagicLinkSignup_Subject"),
            Lf("Email_MagicLinkSignup_Body", magicLinkUrl)));

    public EmailContent RenderWorkspaceCredentials(string userName, string workspaceEmail, string tempPassword, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            L("Email_WorkspaceCredentials_Subject"),
            Lf("Email_WorkspaceCredentials_Body", HtmlEncode(userName), HtmlEncode(workspaceEmail), HtmlEncode(tempPassword))));

    private string L(string key) => _localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, _localizer[key].Value, args);

    private CultureScope WithCulture(string? culture)
    {
        return new CultureScope(culture, _logger);
    }

    private EmailContent RenderLocalized(string? culture, Func<EmailContent> render)
    {
        using (WithCulture(culture))
        {
            return render();
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo? _originalCulture;
        private readonly CultureInfo? _originalUICulture;

        public CultureScope(string? culture, ILogger<EmailRenderer> logger)
        {
            if (string.IsNullOrWhiteSpace(culture)) return;

            try
            {
                _originalCulture = CultureInfo.CurrentCulture;
                _originalUICulture = CultureInfo.CurrentUICulture;
                var targetCulture = new CultureInfo(culture);
                CultureInfo.CurrentUICulture = targetCulture;
                CultureInfo.CurrentCulture = targetCulture;
            }
            catch (CultureNotFoundException ex)
            {
                logger.LogWarning(ex, "Invalid email culture '{Culture}', using current culture fallback", culture);
                _originalCulture = null;
                _originalUICulture = null;
            }
        }

        public void Dispose()
        {
            if (_originalUICulture is not null)
            {
                CultureInfo.CurrentUICulture = _originalUICulture;
                CultureInfo.CurrentCulture = _originalCulture!;
            }
        }
    }

    private static string HtmlEncode(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text);
    }
}
