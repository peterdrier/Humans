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
        var subject = string.Format(CultureInfo.CurrentCulture, _localizer["Email_ApplicationSubmitted_Subject"].Value, applicantName);
        var body = string.Format(
            CultureInfo.CurrentCulture,
            _localizer["Email_ApplicationSubmitted_Body"].Value,
            HtmlEncode(applicantName),
            applicationId,
            _settings.BaseUrl);
        return new EmailContent(subject, body);
    }

    public EmailContent RenderApplicationApproved(string userName, MembershipTier tier, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_ApplicationApproved_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_ApplicationApproved_Body"].Value,
                HtmlEncode(userName),
                tier,
                _settings.BaseUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderApplicationRejected(string userName, MembershipTier tier, string reason, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_ApplicationRejected_Subject"].Value;
            var reasonHtml = string.IsNullOrEmpty(reason)
                ? ""
                : string.Format(CultureInfo.CurrentCulture, _localizer["Email_ReasonLine"].Value, HtmlEncode(reason));
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_ApplicationRejected_Body"].Value,
                HtmlEncode(userName),
                tier,
                reasonHtml,
                _settings.AdminAddress);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderSignupRejected(string userName, string? reason, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_SignupRejected_Subject"].Value;
            var reasonHtml = string.IsNullOrEmpty(reason)
                ? ""
                : string.Format(CultureInfo.CurrentCulture, _localizer["Email_ReasonLine"].Value, HtmlEncode(reason));
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_SignupRejected_Body"].Value,
                HtmlEncode(userName),
                reasonHtml,
                _settings.AdminAddress);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderReConsentsRequired(string userName, IReadOnlyList<string> documentNames, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = documentNames.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, _localizer["Email_ReConsentRequired_Subject_Single"].Value, documentNames[0])
                : _localizer["Email_ReConsentRequired_Subject_Multiple"].Value;
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li><strong>{HtmlEncode(d)}</strong></li>"));
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_ReConsentsRequired_Body"].Value,
                HtmlEncode(userName),
                docsHtml,
                _settings.BaseUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderReConsentReminder(string userName, IReadOnlyList<string> documentNames, int daysRemaining, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = string.Format(CultureInfo.CurrentCulture, _localizer["Email_ReConsentReminder_Subject"].Value, daysRemaining);
            var docsHtml = string.Join("\n", documentNames.Select(d => $"<li>{HtmlEncode(d)}</li>"));
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_ReConsentReminder_Body"].Value,
                HtmlEncode(userName),
                daysRemaining,
                docsHtml,
                _settings.BaseUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderWelcome(string userName, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_Welcome_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_Welcome_Body"].Value,
                HtmlEncode(userName),
                _settings.BaseUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderAccessSuspended(string userName, string reason, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_AccessSuspended_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_AccessSuspended_Body"].Value,
                HtmlEncode(userName),
                HtmlEncode(reason),
                _settings.BaseUrl,
                _settings.AdminAddress);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderEmailVerification(string userName, string toEmail, string verificationUrl, bool isConflict = false, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var templateKey = isConflict
                ? "Email_EmailVerification_Merge_Body"
                : "Email_EmailVerification_Body";
            var subject = _localizer["Email_VerifyEmail_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer[templateKey].Value,
                HtmlEncode(userName),
                HtmlEncode(toEmail),
                verificationUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderAccountDeletionRequested(string userName, string formattedDeletionDate, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_DeletionRequested_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_AccountDeletionRequested_Body"].Value,
                HtmlEncode(userName),
                formattedDeletionDate,
                _settings.BaseUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderAccountDeleted(string userName, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_AccountDeleted_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_AccountDeleted_Body"].Value,
                HtmlEncode(userName));
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderAddedToTeam(string userName, string teamName, string teamSlug, IReadOnlyList<(string Name, string? Url)> resources, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = string.Format(CultureInfo.CurrentCulture, _localizer["Email_AddedToTeam_Subject"].Value, teamName);
            var teamUrl = $"{_settings.BaseUrl}/Teams/{teamSlug}";
            var resourcesHtml = resources.Count > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    _localizer["Email_ResourcesSection"].Value,
                    string.Join("\n", resources.Select(r =>
                        !string.IsNullOrEmpty(r.Url)
                            ? $"<li><a href=\"{r.Url}\">{HtmlEncode(r.Name)}</a></li>"
                            : $"<li>{HtmlEncode(r.Name)}</li>")))
                : "";
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_AddedToTeam_Body"].Value,
                HtmlEncode(userName),
                HtmlEncode(teamName),
                resourcesHtml,
                teamUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderTermRenewalReminder(string userName, string tierName, string expiresAt, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = string.Format(CultureInfo.CurrentCulture, _localizer["Email_TermRenewalReminder_Subject"].Value, tierName);
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_TermRenewalReminder_Body"].Value,
                HtmlEncode(userName),
                HtmlEncode(tierName),
                HtmlEncode(expiresAt),
                _settings.BaseUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderBoardDailyDigest(string boardMemberName, string date, IReadOnlyList<BoardDigestTierGroup> tierGroups, BoardDigestOutstandingCounts? outstandingCounts = null, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDailyDigest_Subject"].Value, date);

            var outstandingHtml = BuildOutstandingSection(outstandingCounts);

            string approvalsHtml;
            if (tierGroups.Count > 0)
            {
                var tierSectionsHtml = string.Join("\n", tierGroups.Select(g =>
                    $"<h3>{HtmlEncode(g.TierLabel)}</h3>\n<ul>\n{string.Join("\n", g.DisplayNames.Select(n => $"<li>{HtmlEncode(n)}</li>"))}\n</ul>"));
                approvalsHtml = tierSectionsHtml;
            }
            else
            {
                approvalsHtml = $"<p>{_localizer["Email_BoardDigest_NoApprovals"].Value}</p>";
            }

            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_BoardDailyDigest_Body"].Value,
                HtmlEncode(boardMemberName),
                HtmlEncode(date),
                approvalsHtml,
                outstandingHtml);
            return new EmailContent(subject, body);
        }
    }

    private string BuildOutstandingSection(BoardDigestOutstandingCounts? counts)
    {
        if (counts is null) return "";

        var hasAny = counts.OnboardingReview > 0 || counts.StillOnboarding > 0
            || counts.BoardVotingTotal > 0 || counts.TeamJoinRequests > 0
            || counts.PendingConsents > 0 || counts.PendingDeletions > 0;
        if (!hasAny) return "";

        var items = new List<string>();

        if (counts.OnboardingReview > 0)
        {
            var text = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDigest_OnboardingReview"].Value, counts.OnboardingReview);
            items.Add($"<li>{text} <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");
        }

        if (counts.StillOnboarding > 0)
        {
            var text = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDigest_StillOnboarding"].Value, counts.StillOnboarding);
            items.Add($"<li>{text}</li>");
        }

        if (counts.BoardVotingTotal > 0)
        {
            var text = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDigest_BoardVoting"].Value, counts.BoardVotingTotal, counts.BoardVotingYours);
            items.Add($"<li>{text} <a href=\"{_settings.BaseUrl}/Applications\">&rarr;</a></li>");
        }

        if (counts.TeamJoinRequests > 0)
        {
            var text = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDigest_TeamJoinRequests"].Value, counts.TeamJoinRequests);
            items.Add($"<li>{text} <a href=\"{_settings.BaseUrl}/Admin\">&rarr;</a></li>");
        }

        if (counts.PendingConsents > 0)
        {
            var text = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDigest_PendingConsents"].Value, counts.PendingConsents);
            items.Add($"<li>{text}</li>");
        }

        if (counts.PendingDeletions > 0)
        {
            var text = string.Format(CultureInfo.CurrentCulture, _localizer["Email_BoardDigest_PendingDeletions"].Value, counts.PendingDeletions);
            items.Add($"<li>{text}</li>");
        }

        var header = _localizer["Email_BoardDigest_OutstandingHeader"].Value;
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
            items.Add($"<li><strong>{counts.PendingConsents}</strong> with outstanding consent requirements</li>");

        if (counts.TeamJoinRequests > 0)
            items.Add($"<li><strong>{counts.TeamJoinRequests}</strong> team join requests pending <a href=\"{_settings.BaseUrl}/Admin\">&rarr;</a></li>");

        if (counts.OnboardingReview > 0)
            items.Add($"<li><strong>{counts.OnboardingReview}</strong> awaiting onboarding review <a href=\"{_settings.BaseUrl}/OnboardingReview\">&rarr;</a></li>");

        if (counts.StillOnboarding > 0)
            items.Add($"<li><strong>{counts.StillOnboarding}</strong> still completing onboarding</li>");

        if (counts.BoardVotingTotal > 0)
            items.Add($"<li><strong>{counts.BoardVotingTotal}</strong> tier applications awaiting vote</li>");

        if (counts.FailedSyncOutboxEvents > 0)
            items.Add($"<li><strong>{counts.FailedSyncOutboxEvents}</strong> failed Google sync outbox events</li>");

        if (counts.TicketSyncError)
            items.Add($"<li>Ticket sync error: {HtmlEncode(counts.TicketSyncErrorMessage ?? "Unknown")}</li>");

        var itemsHtml = items.Count > 0
            ? $"<ul>\n{string.Join("\n", items)}\n</ul>"
            : "<p>All clear — no pending items.</p>";

        var body = $"<h2>Admin Digest — {HtmlEncode(date)}</h2>\n<p>Hi {HtmlEncode(adminName)},</p>\n<h3>Pending Actions &amp; System Health</h3>\n{itemsHtml}";
        return new EmailContent(subject, body);
    }

    public EmailContent RenderFeedbackResponse(string userName, string originalDescription, string responseMessage, string reportLink, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_FeedbackResponse_Subject"].Value;
            var responseHtml = Markdig.Markdown.ToHtml(responseMessage);
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_FeedbackResponse_Body"].Value,
                HtmlEncode(userName),
                HtmlEncode(originalDescription),
                responseHtml,
                HtmlEncode(reportLink));
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderFacilitatedMessage(
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_FacilitatedMessage_Subject"].Value,
                senderName);

            var sanitizedMessage = HtmlEncode(messageText).Replace("\n", "<br />", StringComparison.Ordinal);

            var contactInfoHtml = includeContactInfo && !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{HtmlEncode(senderName)}</strong> &mdash; <a href=\"mailto:{HtmlEncode(senderEmail)}\">{HtmlEncode(senderEmail)}</a></p>"
                : $"<p><em>{HtmlEncode(_localizer["Email_FacilitatedMessage_NoContactInfo"].Value)}</em></p>";

            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_FacilitatedMessage_Body"].Value,
                HtmlEncode(recipientName),
                HtmlEncode(senderName),
                sanitizedMessage,
                contactInfoHtml);

            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderMagicLinkLogin(string displayName, string magicLinkUrl, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_MagicLinkLogin_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_MagicLinkLogin_Body"].Value,
                HtmlEncode(displayName),
                magicLinkUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderMagicLinkSignup(string magicLinkUrl, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_MagicLinkSignup_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_MagicLinkSignup_Body"].Value,
                magicLinkUrl);
            return new EmailContent(subject, body);
        }
    }

    public EmailContent RenderWorkspaceCredentials(string userName, string workspaceEmail, string tempPassword, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_WorkspaceCredentials_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_WorkspaceCredentials_Body"].Value,
                HtmlEncode(userName),
                HtmlEncode(workspaceEmail),
                HtmlEncode(tempPassword));
            return new EmailContent(subject, body);
        }
    }

    private CultureScope WithCulture(string? culture)
    {
        return new CultureScope(culture, _logger);
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
