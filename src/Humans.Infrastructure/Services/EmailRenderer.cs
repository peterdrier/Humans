using System.Globalization;
using Humans.Application.Interfaces.Email;
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
public class EmailRenderer(
    IOptions<EmailSettings> settings,
    IStringLocalizerFactory localizerFactory,
    ILogger<EmailRenderer> logger) : IEmailRenderer
{
    private readonly EmailSettings _settings = settings.Value;
    private readonly IStringLocalizer _localizer = localizerFactory.Create("SharedResource", "Humans.Web");

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

    public EmailContent RenderFeedbackResponse(string userName, string originalDescription, string responseMessage, string reportLink, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var responseHtml = Markdig.Markdown.ToHtml(responseMessage);
            return new EmailContent(
                L("Email_FeedbackResponse_Subject"),
                Lf("Email_FeedbackResponse_Body", HtmlEncode(userName), HtmlEncode(originalDescription), responseHtml, HtmlEncode(reportLink)));
        });

    public EmailContent RenderIssueComment(string displayName, string issueTitle, string commentContent, string issueLink, string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var commentHtml = Markdig.Markdown.ToHtml(commentContent);
            var fullLink = issueLink.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? issueLink
                : $"{_settings.BaseUrl.TrimEnd('/')}{(issueLink.StartsWith('/') ? "" : "/")}{issueLink}";
            return new EmailContent(
                Lf("Email_IssueComment_Subject", HtmlEncode(issueTitle)),
                Lf("Email_IssueComment_Body", HtmlEncode(displayName), HtmlEncode(issueTitle), commentHtml, HtmlEncode(fullLink)));
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

    public EmailContent RenderCoordinatorRotaMessage(
        string recipientName,
        string senderName,
        string? senderEmail,
        string rotaName,
        string messageText,
        IReadOnlyList<string> shiftLines,
        string? culture = null)
        => RenderLocalized(culture, () =>
        {
            ArgumentNullException.ThrowIfNull(shiftLines);

            var sanitizedMessage = HtmlEncode(messageText).Replace("\n", "<br />", StringComparison.Ordinal);

            var shiftListHtml = shiftLines.Count == 0
                ? $"<p><em>{HtmlEncode(L("Email_CoordinatorRotaMessage_NoShifts"))}</em></p>"
                : "<ul>" + string.Concat(shiftLines.Select(line => $"<li>{HtmlEncode(line)}</li>")) + "</ul>";

            var senderLine = !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{HtmlEncode(senderName)}</strong> &mdash; <a href=\"mailto:{HtmlEncode(senderEmail)}\">{HtmlEncode(senderEmail)}</a></p>"
                : $"<p><strong>{HtmlEncode(senderName)}</strong></p>";

            return new EmailContent(
                Lf("Email_CoordinatorRotaMessage_Subject", HtmlEncode(rotaName)),
                Lf("Email_CoordinatorRotaMessage_Body",
                    HtmlEncode(recipientName),
                    HtmlEncode(senderName),
                    HtmlEncode(rotaName),
                    sanitizedMessage,
                    shiftListHtml,
                    senderLine));
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
        return new CultureScope(culture, logger);
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

    public EmailContent RenderGoogleGroupRemovalLossOfAccess(
        string userName,
        string groupName,
        string groupEmail,
        string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            Lf("Email_GoogleGroupRemoval_LossOfAccess_Subject", HtmlEncode(groupEmail)),
            Lf("Email_GoogleGroupRemoval_LossOfAccess_Body",
                HtmlEncode(userName), HtmlEncode(groupName), HtmlEncode(groupEmail))));

    public EmailContent RenderGoogleDriveRemovalLossOfAccess(
        string userName,
        string folderName,
        string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            Lf("Email_GoogleDriveRemoval_LossOfAccess_Subject", HtmlEncode(folderName)),
            Lf("Email_GoogleDriveRemoval_LossOfAccess_Body",
                HtmlEncode(userName), HtmlEncode(folderName))));

    public EmailContent RenderGoogleAccessRemovalSecondaryCleanup(
        string userName,
        string removedEmail,
        string currentGoogleEmail,
        string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            Lf("Email_GoogleAccessRemoval_SecondaryCleanup_Subject", HtmlEncode(removedEmail)),
            Lf("Email_GoogleAccessRemoval_SecondaryCleanup_Body",
                HtmlEncode(userName), HtmlEncode(removedEmail), HtmlEncode(currentGoogleEmail))));

    public EmailContent RenderCampaignCode(string subject, string markdownBody, string code, string recipientName)
    {
        // HTML-encode the substitutions so malicious codes/names cannot inject markup.
        var encodedCode = HtmlEncode(code);
        var encodedName = HtmlEncode(recipientName);

        var markdown = markdownBody
            .Replace("{{Code}}", encodedCode, StringComparison.Ordinal)
            .Replace("{{Name}}", encodedName, StringComparison.Ordinal);
        var renderedBody = Markdig.Markdown.ToHtml(markdown);

        // Subject is a plain-text field; no HTML encoding required.
        var renderedSubject = subject
            .Replace("{{Code}}", code, StringComparison.Ordinal)
            .Replace("{{Name}}", recipientName, StringComparison.Ordinal);

        return new EmailContent(renderedSubject, renderedBody);
    }

    public EmailContent RenderEventLifecycle(EventLifecycleNotification request, string? culture = null)
    {
        using (WithCulture(culture ?? request.Culture))
        {
            var userName = HtmlEncode(request.UserName);
            var eventTitle = HtmlEncode(request.EventTitle);
            var reason = HtmlEncode(request.Reason ?? string.Empty);
            var actionUrl = HtmlEncode(request.ActionUrl ?? string.Empty);

            return request.NewStatus switch
            {
                EventStatus.Pending => new EmailContent(
                    "Your event submission has been received",
                    $"""
                        <p>Hi {userName},</p>
                        <p>Your event <strong>{eventTitle}</strong> has been received and is now in the moderation queue.
                        You will be notified once it has been reviewed.</p>
                        <p><a href="{actionUrl}">View your submissions</a></p>
                        """),
                EventStatus.Approved => new EmailContent(
                    "Your event has been approved",
                    $"""
                        <p>Hi {userName},</p>
                        <p>Your event <strong>{eventTitle}</strong> has been approved and will appear in the event guide.</p>
                        """),
                EventStatus.Rejected => new EmailContent(
                    "Your event submission was not approved",
                    $"""
                        <p>Hi {userName},</p>
                        <p>Your event <strong>{eventTitle}</strong> was not approved for the event guide.</p>
                        <p><strong>Reason:</strong> {reason}</p>
                        <p>You can edit and resubmit your event here: <a href="{actionUrl}">Edit event</a></p>
                        """),
                EventStatus.ResubmitRequested => new EmailContent(
                    "Changes requested for your event submission",
                    $"""
                        <p>Hi {userName},</p>
                        <p>The moderation team has requested changes to your event <strong>{eventTitle}</strong> before it can be approved.</p>
                        <p><strong>Feedback:</strong> {reason}</p>
                        <p>Please update and resubmit here: <a href="{actionUrl}">Edit event</a></p>
                        """),
                _ => throw new ArgumentOutOfRangeException(nameof(request),
                    $"EventLifecycleNotification does not support status {request.NewStatus}")
            };
        }
    }
}
