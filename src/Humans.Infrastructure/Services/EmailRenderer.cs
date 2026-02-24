using System.Globalization;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Localization;
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

    public EmailRenderer(
        IOptions<EmailSettings> settings,
        IStringLocalizerFactory localizerFactory)
    {
        _settings = settings.Value;
        _localizer = localizerFactory.Create("SharedResource", "Humans.Web");
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

    public EmailContent RenderEmailVerification(string userName, string toEmail, string verificationUrl, string? culture = null)
    {
        using (WithCulture(culture))
        {
            var subject = _localizer["Email_VerifyEmail_Subject"].Value;
            var body = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Email_EmailVerification_Body"].Value,
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
        if (counts == null) return "";

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

    private static CultureScope WithCulture(string? culture)
    {
        return new CultureScope(culture);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo? _originalCulture;
        private readonly CultureInfo? _originalUICulture;

        public CultureScope(string? culture)
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
            catch (CultureNotFoundException)
            {
                _originalCulture = null;
                _originalUICulture = null;
            }
        }

        public void Dispose()
        {
            if (_originalUICulture != null)
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
