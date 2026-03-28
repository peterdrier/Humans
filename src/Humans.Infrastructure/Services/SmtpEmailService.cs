using Humans.Application.DTOs;
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

namespace Humans.Infrastructure.Services;

/// <summary>
/// Email service implementation using SMTP via MailKit.
/// Delegates rendering to IEmailRenderer; handles transport only.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly IHumansMetrics _metrics;
    private readonly ILogger<SmtpEmailService> _logger;
    private readonly IEmailRenderer _renderer;
    private readonly string _environmentName;

    public SmtpEmailService(
        IOptions<EmailSettings> settings,
        IHumansMetrics metrics,
        ILogger<SmtpEmailService> logger,
        IEmailRenderer renderer,
        IHostEnvironment hostEnvironment)
    {
        _settings = settings.Value;
        _metrics = metrics;
        _logger = logger;
        _renderer = renderer;
        _environmentName = hostEnvironment.EnvironmentName;
    }

    /// <inheritdoc />
    public async Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderApplicationApproved(userName, tier, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("application_approved");
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
        var content = _renderer.RenderApplicationRejected(userName, tier, reason, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("application_rejected");
    }

    /// <inheritdoc />
    public async Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        await SendReConsentsRequiredAsync(userEmail, userName, new[] { documentName }, culture, cancellationToken);
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
        var content = _renderer.RenderReConsentsRequired(userName, docs, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("reconsents_required");
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
        var content = _renderer.RenderReConsentReminder(userName, docs, daysRemaining, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("reconsent_reminder");
    }

    /// <inheritdoc />
    public async Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderWelcome(userName, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("welcome");
    }

    /// <inheritdoc />
    public async Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderAccessSuspended(userName, reason, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("access_suspended");
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
        var content = _renderer.RenderEmailVerification(userName, toEmail, verificationUrl, isConflict, culture);
        await SendEmailAsync(toEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("email_verification");
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
        var content = _renderer.RenderAccountDeletionRequested(userName, formattedDate, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("deletion_requested");
    }

    /// <inheritdoc />
    public async Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderAccountDeleted(userName, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("account_deleted");
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
        var content = _renderer.RenderAddedToTeam(userName, teamName, teamSlug, resourceList, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("added_to_team");
    }

    /// <inheritdoc />
    public async Task SendSignupRejectedAsync(
        string userEmail,
        string userName,
        string? reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderSignupRejected(userName, reason, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("signup_rejected");
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
        var content = _renderer.RenderTermRenewalReminder(userName, tierName, expiresAt, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("term_renewal_reminder");
    }

    /// <inheritdoc />
    public async Task SendBoardDailyDigestAsync(
        string email,
        string name,
        string date,
        IReadOnlyList<BoardDigestTierGroup> groups,
        BoardDigestOutstandingCounts? outstandingCounts = null,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderBoardDailyDigest(name, date, groups, outstandingCounts, culture);
        await SendEmailAsync(email, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("board_daily_digest");
    }

    /// <inheritdoc />
    public async Task SendAdminDailyDigestAsync(
        string email,
        string name,
        string date,
        AdminDigestCounts counts,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderAdminDailyDigest(name, date, counts, culture);
        await SendEmailAsync(email, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("admin_daily_digest");
    }

    /// <inheritdoc />
    public async Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string reportLink, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, reportLink, culture);
        await SendEmailAsync(userEmail, content.Subject, content.HtmlBody, cancellationToken: cancellationToken);
        _metrics.RecordEmailSent("feedback_response");
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
        var content = _renderer.RenderFacilitatedMessage(
            recipientName, senderName, messageText, includeContactInfo, senderEmail, culture);
        var replyTo = includeContactInfo ? senderEmail : null;
        await SendEmailAsync(recipientEmail, content.Subject, content.HtmlBody, cancellationToken, replyTo);
        _metrics.RecordEmailSent("facilitated_message");
    }

    public async Task SendMagicLinkLoginAsync(
        string toEmail, string displayName, string magicLinkUrl,
        string? culture = null, CancellationToken ct = default)
    {
        var content = _renderer.RenderMagicLinkLogin(displayName, magicLinkUrl, culture);
        await SendEmailAsync(toEmail, content.Subject, content.HtmlBody, ct);
        _metrics.RecordEmailSent("magic_link_login");
    }

    public async Task SendMagicLinkSignupAsync(
        string toEmail, string magicLinkUrl,
        string? culture = null, CancellationToken ct = default)
    {
        var content = _renderer.RenderMagicLinkSignup(magicLinkUrl, culture);
        await SendEmailAsync(toEmail, content.Subject, content.HtmlBody, ct);
        _metrics.RecordEmailSent("magic_link_signup");
    }

    public async Task SendWorkspaceCredentialsAsync(
        string recoveryEmail, string userName, string workspaceEmail, string tempPassword,
        string? culture = null, CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderWorkspaceCredentials(userName, workspaceEmail, tempPassword, culture);
        await SendEmailAsync(recoveryEmail, content.Subject, content.HtmlBody, cancellationToken);
        _metrics.RecordEmailSent("workspace_credentials");
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

            _logger.LogInformation("Email sent to {To}: {Subject}", toAddress, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}: {Subject}", toAddress, subject);
            throw;
        }
    }
}
