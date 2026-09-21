using Humans.Users.Contracts;
using System.Globalization;
using Humans.Email.Contracts;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Humans.Email.Services;

/// <summary>
/// Renders email subject + body HTML for all system email types.
/// Body text is localized via the section's own EmailResource resx set.
/// </summary>
internal sealed class EmailRenderer(
    IOptions<EmailSettings> settings,
    IStringLocalizer<EmailResource> localizer,
    ILogger<EmailRenderer> logger) : IEmailRenderer
{
    private readonly EmailSettings _settings = settings.Value;

    public EmailContent RenderApplicationSubmitted(Guid applicationId, string applicantName)
    {
        // Admin email — always English, no culture switch
        return new EmailContent(
            Lf("Email_ApplicationSubmitted_Subject", applicantName),
            Lf("Email_ApplicationSubmitted_Body", HtmlEncode(applicantName), applicationId, _settings.BaseUrl));
    }

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

    public EmailContent RenderSurveyInvitation(
        string userName,
        string surveyTitle,
        string answerToken,
        string? culture = null,
        string? customSubject = null,
        string? customMessage = null)
        => RenderLocalized(culture, () =>
        {
            var subject = string.IsNullOrWhiteSpace(customSubject)
                ? Lf("Email_SurveyInvitation_Subject", surveyTitle)
                : customSubject.Trim();
            var messageHtml = string.IsNullOrWhiteSpace(customMessage)
                ? $"<p>{Lf("Email_SurveyInvitation_DefaultMessage", HtmlEncode(surveyTitle))}</p>"
                : SanitizedMarkdownRenderer.Render(customMessage.Trim());

            return new EmailContent(
                subject,
                Lf(
                    "Email_SurveyInvitation_Body",
                    HtmlEncode(userName),
                    HtmlEncode(surveyTitle),
                    BuildSurveyAnswerUrl(answerToken),
                    messageHtml));
        });

    public EmailContent RenderSurveyReminder(string userName, string surveyTitle, string answerToken, string? culture = null)
        => RenderLocalized(culture, () => new EmailContent(
            Lf("Email_SurveyReminder_Subject", surveyTitle),
            Lf("Email_SurveyReminder_Body", HtmlEncode(userName), HtmlEncode(surveyTitle), BuildSurveyAnswerUrl(answerToken))));

    private string BuildSurveyAnswerUrl(string token)
        => $"{_settings.BaseUrl.TrimEnd('/')}/Survey/Answer?t={Uri.EscapeDataString(token)}";

    public EmailContent RenderFacilitatedMessage(
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null)
        => RenderLocalized(culture, () =>
        {
            var sanitizedMessage = SanitizedMarkdownRenderer.Render(messageText);

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

            var sanitizedMessage = SanitizedMarkdownRenderer.Render(messageText);

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

    public EmailContent RenderCoordinatorTeamRotasMessage(
        string recipientName,
        string senderName,
        string? senderEmail,
        string teamName,
        string messageText,
        IReadOnlyList<RotaShiftGroup> shiftGroups,
        string? culture = null)
        => RenderLocalized(culture, () =>
        {
            ArgumentNullException.ThrowIfNull(shiftGroups);

            var sanitizedMessage = SanitizedMarkdownRenderer.Render(messageText);

            // Per-rota groups: bold rota name then <ul><li> shift lines.
            // Empty-groups fallback mirrors the per-rota renderer.
            string shiftGroupsHtml;
            if (shiftGroups.Count == 0 || shiftGroups.All(g => g.ShiftLines.Count == 0))
            {
                shiftGroupsHtml = $"<p><em>{HtmlEncode(L("Email_CoordinatorRotaMessage_NoShifts"))}</em></p>";
            }
            else
            {
                shiftGroupsHtml = string.Concat(shiftGroups.Select(g =>
                {
                    var lines = g.ShiftLines.Count == 0
                        ? string.Empty
                        : "<ul>" + string.Concat(g.ShiftLines.Select(line => $"<li>{HtmlEncode(line)}</li>")) + "</ul>";
                    return $"<p><strong>{HtmlEncode(g.RotaName)}</strong></p>{lines}";
                }));
            }

            var senderLine = !string.IsNullOrEmpty(senderEmail)
                ? $"<p><strong>{HtmlEncode(senderName)}</strong> &mdash; <a href=\"mailto:{HtmlEncode(senderEmail)}\">{HtmlEncode(senderEmail)}</a></p>"
                : $"<p><strong>{HtmlEncode(senderName)}</strong></p>";

            return new EmailContent(
                Lf("Email_CoordinatorTeamRotasMessage_Subject", HtmlEncode(teamName)),
                Lf("Email_CoordinatorTeamRotasMessage_Body",
                    HtmlEncode(recipientName),
                    HtmlEncode(senderName),
                    HtmlEncode(teamName),
                    sanitizedMessage,
                    shiftGroupsHtml,
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

    private string L(string key) => localizer[key].Value;

    private string Lf(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, localizer[key].Value, args);

    private EmailContent RenderLocalized(string? culture, Func<EmailContent> render)
    {
        using (new CultureScope(culture, logger))
        {
            return render();
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

    public EmailContent RenderTicketTransferRequested(
        string senderName, string receiverName, string ticketLabel, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var name = HtmlEncode(senderName);
            var receiver = HtmlEncode(receiverName);
            var ticket = HtmlEncode(ticketLabel);
            return new EmailContent(
                "Ticket transfer requested",
                $"""
                    <p>Hi {name},</p>
                    <p>We've received your request to transfer ticket <strong>{ticket}</strong> to <strong>{receiver}</strong>.</p>
                    <p>Our ticketing team will process this and let you know shortly. No further action is needed from you.</p>
                    """);
        }
    }

    public EmailContent RenderTicketTransferTeamNotification(
        string senderName, string receiverName, string receiverEmail,
        string ticketLabel, string? reason, string reviewUrl)
    {
        var sender = HtmlEncode(senderName);
        var receiver = HtmlEncode(receiverName);
        var email = HtmlEncode(receiverEmail);
        var ticket = HtmlEncode(ticketLabel);
        var reasonHtml = string.IsNullOrWhiteSpace(reason)
            ? ""
            : $"<p><strong>Reason given:</strong> {HtmlEncode(reason)}</p>";
        var fullUrl = reviewUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? reviewUrl
            : $"{_settings.BaseUrl.TrimEnd('/')}{(reviewUrl.StartsWith('/') ? "" : "/")}{reviewUrl}";
        return new EmailContent(
            "Ticket transfer to process",
            $"""
                <p>A new ticket transfer is awaiting manual processing in TicketTailor.</p>
                <p><strong>From:</strong> {sender}<br>
                <strong>To:</strong> {receiver} &lt;{email}&gt;<br>
                <strong>Ticket:</strong> {ticket}</p>
                {reasonHtml}
                <p>Void the original and reissue to the recipient in TicketTailor, then mark the request
                resolved here: <a href="{HtmlEncode(fullUrl)}">Review transfer</a></p>
                """);
    }

    public EmailContent RenderTicketTransferDecision(
        string toName, bool successful, string ticketLabel, string receiverName,
        string? reason, string? culture = null)
    {
        using (new CultureScope(culture, logger))
        {
            var name = HtmlEncode(toName);
            var receiver = HtmlEncode(receiverName);
            var ticket = HtmlEncode(ticketLabel);
            if (successful)
            {
                return new EmailContent(
                    "Ticket transfer complete",
                    $"""
                        <p>Hi {name},</p>
                        <p>The transfer of ticket <strong>{ticket}</strong> to <strong>{receiver}</strong> is complete.</p>
                        """);
            }
            var reasonHtml = string.IsNullOrWhiteSpace(reason)
                ? ""
                : $"<p><strong>Reason:</strong> {HtmlEncode(reason)}</p>";
            return new EmailContent(
                "Ticket transfer cancelled",
                $"""
                    <p>Hi {name},</p>
                    <p>The requested transfer of ticket <strong>{ticket}</strong> to <strong>{receiver}</strong> was not completed.</p>
                    {reasonHtml}
                    <p>If you have questions, reply to this email and our ticketing team will help.</p>
                    """);
        }
    }

    public EmailContent RenderWorkgroupNotice(WorkgroupNoticeRequest request)
        => RenderLocalized(request.Culture, () =>
        {
            var greeting = string.IsNullOrEmpty(request.RecipientName)
                ? L("Email_WorkgroupNotice_Greeting_Generic")
                : Lf("Email_WorkgroupNotice_Greeting_Named", HtmlEncode(request.RecipientName));
            // Two forms of the same name: subjects are plain text, bodies are HTML. Encoding
            // once for both would put "Health &amp; Safety" in the subject line.
            var name = request.WorkgroupName;
            var nameHtml = HtmlEncode(request.WorkgroupName);
            var url = $"{_settings.BaseUrl}/Workgroups/{request.WorkgroupSlug}";
            var detail = HtmlEncode(request.Detail ?? "");

            return request.Kind switch
            {
                WorkgroupNoticeKind.Applied => new EmailContent(
                    Lf("Email_WorkgroupNotice_Applied_Subject", name),
                    Lf("Email_WorkgroupNotice_Applied_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Referred => new EmailContent(
                    Lf("Email_WorkgroupNotice_Referred_Subject", name),
                    Lf("Email_WorkgroupNotice_Referred_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Registered => new EmailContent(
                    Lf("Email_WorkgroupNotice_Registered_Subject", name),
                    Lf("Email_WorkgroupNotice_Registered_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Refused => new EmailContent(
                    Lf("Email_WorkgroupNotice_Refused_Subject", name),
                    Lf("Email_WorkgroupNotice_Refused_Body", greeting, nameHtml,
                        string.IsNullOrEmpty(request.Detail) ? "" : Lf("Email_ReasonLine", detail), url)),
                WorkgroupNoticeKind.Withdrawn => new EmailContent(
                    Lf("Email_WorkgroupNotice_Withdrawn_Subject", name),
                    Lf("Email_WorkgroupNotice_Withdrawn_Body", greeting, nameHtml,
                        string.IsNullOrEmpty(request.Detail) ? "" : Lf("Email_ReasonLine", detail), url)),
                WorkgroupNoticeKind.Ended => new EmailContent(
                    Lf("Email_WorkgroupNotice_Ended_Subject", name),
                    Lf("Email_WorkgroupNotice_Ended_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.Reactivated => new EmailContent(
                    Lf("Email_WorkgroupNotice_Reactivated_Subject", name),
                    Lf("Email_WorkgroupNotice_Reactivated_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.CoordinatorsChanged => new EmailContent(
                    Lf("Email_WorkgroupNotice_CoordinatorsChanged_Subject", name),
                    Lf("Email_WorkgroupNotice_CoordinatorsChanged_Body", greeting, nameHtml, url)),
                WorkgroupNoticeKind.DormancyInquiry => new EmailContent(
                    Lf("Email_WorkgroupNotice_DormancyInquiry_Subject", name),
                    Lf("Email_WorkgroupNotice_DormancyInquiry_Body", greeting, nameHtml, detail, url)),
                WorkgroupNoticeKind.Delivered => new EmailContent(
                    Lf("Email_WorkgroupNotice_Delivered_Subject", name),
                    Lf("Email_WorkgroupNotice_Delivered_Body", greeting, nameHtml, detail, url)),
                WorkgroupNoticeKind.DispositionRecorded => new EmailContent(
                    Lf("Email_WorkgroupNotice_DispositionRecorded_Subject", name),
                    Lf("Email_WorkgroupNotice_DispositionRecorded_Body", greeting, nameHtml, detail, url)),
                _ => throw new InvalidOperationException(
                    $"WorkgroupNotice does not support kind {request.Kind}")
            };
        });

    /// <summary>
    /// Webmail has no Humans origin, so a root-relative path in an email resolves against the
    /// reader's own host. Callers pass the in-app path; the absolute link is built here, the
    /// same way survey and team links are.
    /// </summary>
    private string AbsoluteUrl(string path) =>
        $"{_settings.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

}
