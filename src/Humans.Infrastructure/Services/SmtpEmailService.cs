using Humans.Domain.Enums;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Helpers;
using Humans.Application.Interfaces.Email;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Email service implementation using SMTP via MailKit.
/// Delegates rendering to IEmailRenderer; handles transport only.
/// </summary>
public class SmtpEmailService(
    IOptions<EmailSettings> settings,
    IHumansMetrics metrics,
    ILogger<SmtpEmailService> logger,
    IEmailRenderer renderer,
    IHostEnvironment hostEnvironment) : IEmailService
{
    private readonly EmailSettings _settings = settings.Value;
    private readonly string _environmentName = hostEnvironment.EnvironmentName;

    /// <inheritdoc />
    public async Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderApplicationApproved(userName, tier, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("application_approved");
    }

    /// <inheritdoc />
    public async Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderApplicationRejected(userName, tier, reason, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("application_rejected");
    }

    /// <inheritdoc />
    public async Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        await SendReConsentsRequiredAsync(userEmail, userName, [documentName], culture, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var docs = documentNames.ToList();
        var content = renderer.RenderReConsentsRequired(userName, docs, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("reconsents_required");
    }

    /// <inheritdoc />
    public async Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var docs = documentNames.ToList();
        var content = renderer.RenderReConsentReminder(userName, docs, daysRemaining, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("reconsent_reminder");
    }

    /// <inheritdoc />
    public async Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderWelcome(userName, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("welcome");
    }

    /// <inheritdoc />
    public async Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderAccessSuspended(userName, reason, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("access_suspended");
    }

    /// <inheritdoc />
    public async Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        bool isConflict = false,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderEmailVerification(userName, toEmail, verificationUrl, isConflict, culture);
        await SendEmailAsync(toEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("email_verification");
    }

    /// <inheritdoc />
    public async Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var formattedDate = deletionDate.ToInvariantLongDate();
        var content = renderer.RenderAccountDeletionRequested(userName, formattedDate, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("deletion_requested");
    }

    /// <inheritdoc />
    public async Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderAccountDeleted(userName, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("account_deleted");
    }

    /// <inheritdoc />
    public async Task SendAddedToTeamAsync(
        string userEmail,
        string userName,
        string teamName,
        string teamSlug,
        IEnumerable<(string Name, string? Url)> resources,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var resourceList = resources.ToList();
        var content = renderer.RenderAddedToTeam(userName, teamName, teamSlug, resourceList, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("added_to_team");
    }

    /// <inheritdoc />
    public async Task SendSignupRejectedAsync(
        string userEmail,
        string userName,
        string? reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderSignupRejected(userName, reason, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("signup_rejected");
    }

    /// <inheritdoc />
    public async Task SendTermRenewalReminderAsync(
        string userEmail,
        string userName,
        string tierName,
        string expiresAt,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderTermRenewalReminder(userName, tierName, expiresAt, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("term_renewal_reminder");
    }

    /// <inheritdoc />
    public async Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string reportLink, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, reportLink, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken: cancellationToken);
        metrics.RecordEmailSent("feedback_response");
    }

    public async Task SendFacilitatedMessageAsync(
        string recipientEmail,
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderFacilitatedMessage(
            recipientName, senderName, messageText, includeContactInfo, senderEmail, culture);
        var replyTo = includeContactInfo ? senderEmail : null;
        await SendEmailAsync(recipientEmail, content.Subject, content.HtmlBody, cancellationToken, replyTo);
        metrics.RecordEmailSent("facilitated_message");
    }

    public async Task SendCoordinatorRotaMessageAsync(
        CoordinatorRotaMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = renderer.RenderCoordinatorRotaMessage(
            request.RecipientName, request.SenderName, request.SenderEmail,
            request.RotaName, request.MessageText, request.ShiftLines, request.Culture);
        await SendEmailAsync(request.RecipientEmail, content.Subject, content.HtmlBody, cancellationToken, request.SenderEmail);
        metrics.RecordEmailSent("coordinator_rota_message");
    }

    public async Task SendMagicLinkLoginAsync(
        string toEmail, string displayName, string magicLinkUrl,
        string? culture = null, CancellationToken ct = default)
    {
        var content = renderer.RenderMagicLinkLogin(displayName, magicLinkUrl, culture);
        await SendEmailAsync(toEmail, content.Subject, content.HtmlBody, ct);
        metrics.RecordEmailSent("magic_link_login");
    }

    public async Task SendMagicLinkSignupAsync(
        string toEmail, string magicLinkUrl,
        string? culture = null, CancellationToken ct = default)
    {
        var content = renderer.RenderMagicLinkSignup(magicLinkUrl, culture);
        await SendEmailAsync(toEmail, content.Subject, content.HtmlBody, ct);
        metrics.RecordEmailSent("magic_link_signup");
    }

    public async Task SendWorkspaceCredentialsAsync(
        string recoveryEmail, string userName, string workspaceEmail, string tempPassword,
        string? culture = null, CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderWorkspaceCredentials(userName, workspaceEmail, tempPassword, culture);
        await SendEmailAsync(recoveryEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("workspace_credentials");
    }

    public async Task SendCampaignCodeAsync(CampaignCodeEmailRequest request, CancellationToken cancellationToken = default)
    {
        // SmtpEmailService sends inline (no outbox). Render the campaign markdown
        // body with {{Code}}/{{Name}} placeholders and deliver via SMTP. We HTML-
        // encode the substitutions so content cannot inject markup.
        var encodedCode = System.Net.WebUtility.HtmlEncode(request.Code);
        var encodedName = System.Net.WebUtility.HtmlEncode(request.RecipientName);

        var markdown = request.MarkdownBody
            .Replace("{{Code}}", encodedCode, StringComparison.Ordinal)
            .Replace("{{Name}}", encodedName, StringComparison.Ordinal);
        var renderedBody = Markdig.Markdown.ToHtml(markdown);

        var renderedSubject = request.Subject
            .Replace("{{Code}}", request.Code, StringComparison.Ordinal)
            .Replace("{{Name}}", request.RecipientName, StringComparison.Ordinal);

        await SendEmailAsync(request.RecipientEmail, renderedSubject, renderedBody, cancellationToken, request.ReplyTo);
        metrics.RecordEmailSent("campaign_code");
    }

    /// <inheritdoc />
    public async Task SendEventLifecycleNotificationAsync(
        EventLifecycleNotification request,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderEventLifecycle(request);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent(request.TemplateName());
    }

    /// <inheritdoc />
    public async Task SendIssueCommentAsync(
        string to,
        string displayName,
        string issueTitle,
        string commentContent,
        string issueLink,
        string preferredLanguage,
        CancellationToken ct = default)
    {
        var content = renderer.RenderIssueComment(displayName, issueTitle, commentContent, issueLink, preferredLanguage);
        await SendEmailAsync(to, content.Subject, content.HtmlBody, ct);
        metrics.RecordEmailSent("issue_comment");
    }

    public async Task SendGoogleGroupRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string groupName,
        string groupEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderGoogleGroupRemovalLossOfAccess(userName, groupName, groupEmail, culture);
        await SendEmailAsync(removedEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("google_group_removal_loss_of_access");
    }

    public async Task SendGoogleDriveRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string folderName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderGoogleDriveRemovalLossOfAccess(userName, folderName, culture);
        await SendEmailAsync(removedEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("google_drive_removal_loss_of_access");
    }

    public async Task SendGoogleAccessRemovalSecondaryCleanupAsync(
        string removedEmail,
        string userName,
        string currentGoogleEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = renderer.RenderGoogleAccessRemovalSecondaryCleanup(
            userName, removedEmail, currentGoogleEmail, culture);
        await SendEmailAsync(removedEmail, content.Subject, content.HtmlBody, cancellationToken);
        metrics.RecordEmailSent("google_access_removal_secondary_cleanup");
    }

    private async Task SendEmailAsync(
        string toAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken,
        string? replyTo = null)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;

            if (!string.IsNullOrEmpty(replyTo))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
            }

            var (wrappedHtml, plainText) = EmailBodyComposer.Compose(htmlBody, _settings.BaseUrl, _environmentName);

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = wrappedHtml,
                TextBody = plainText
            };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();

            await client.ConnectAsync(
                _settings.SmtpHost,
                _settings.SmtpPort,
                _settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            logger.LogInformation("Email sent to {To}: {Subject}", toAddress, subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email to {To}: {Subject}", toAddress, subject);
            throw;
        }
    }
}
