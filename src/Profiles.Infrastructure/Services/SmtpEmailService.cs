using System.Globalization;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Profiles.Application.Interfaces;
using Profiles.Infrastructure.Configuration;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Email service implementation using SMTP via MailKit.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(
        IOptions<EmailSettings> settings,
        ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendApplicationSubmittedAsync(
        Guid applicationId,
        string applicantName,
        CancellationToken cancellationToken = default)
    {
        var subject = $"New Membership Application: {applicantName}";
        var body = $"""
            <h2>New Membership Application</h2>
            <p>A new membership application has been submitted.</p>
            <ul>
                <li><strong>Applicant:</strong> {HtmlEncode(applicantName)}</li>
                <li><strong>Application ID:</strong> {applicationId}</li>
            </ul>
            <p><a href="{_settings.BaseUrl}/Admin/Applications">Review Application</a></p>
            """;

        await SendEmailAsync(_settings.AdminAddress, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var subject = "Welcome to Nobodies Collective!";
        var body = $"""
            <h2>Your Application Has Been Approved!</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>We're delighted to inform you that your membership application has been approved.
            Welcome to Nobodies Collective!</p>
            <p>You can now access your member profile and explore teams:</p>
            <ul>
                <li><a href="{_settings.BaseUrl}/Profile">View Your Profile</a></li>
                <li><a href="{_settings.BaseUrl}/Teams">Browse Teams</a></li>
                <li><a href="{_settings.BaseUrl}/Consent">Review Legal Documents</a></li>
            </ul>
            <p>If you have any questions, don't hesitate to reach out.</p>
            <p>Welcome aboard!<br/>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var subject = "Regarding Your Membership Application";
        var body = $"""
            <h2>Application Update</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>Thank you for your interest in Nobodies Collective. After careful review,
            we regret to inform you that we are unable to approve your membership application at this time.</p>
            {(string.IsNullOrEmpty(reason) ? "" : $"<p><strong>Reason:</strong> {HtmlEncode(reason)}</p>")}
            <p>If you have any questions or would like to discuss this decision,
            please contact us at <a href="mailto:{_settings.AdminAddress}">{_settings.AdminAddress}</a>.</p>
            <p>Best regards,<br/>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        CancellationToken cancellationToken = default)
    {
        await SendReConsentsRequiredAsync(userEmail, userName, new[] { documentName }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        CancellationToken cancellationToken = default)
    {
        var docs = documentNames.ToList();
        var subject = docs.Count == 1 
            ? $"Action Required: Updated {docs[0]}" 
            : "Action Required: Multiple Legal Documents Updated";

        var body = $"""
            <h2>Legal Document Update</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>We have updated the following required documents:</p>
            <ul>
                {string.Join("\n", docs.Select(d => $"<li><strong>{HtmlEncode(d)}</strong></li>"))}
            </ul>
            <p>As a member, you need to review and accept these updated documents to maintain your active membership status.</p>
            <p><a href="{_settings.BaseUrl}/Consent">Review and Accept</a></p>
            <p>If you have any questions about the changes, please contact us.</p>
            <p>Thank you,<br/>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        CancellationToken cancellationToken = default)
    {
        var docs = string.Join(", ", documentNames);
        var subject = $"Reminder: {daysRemaining} days to review updated documents";
        var body = $"""
            <h2>Consent Reminder</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>This is a reminder that you have <strong>{daysRemaining} days</strong> remaining to review and accept
            the following updated documents:</p>
            <ul>
                {string.Join("\n", documentNames.Select(d => $"<li>{HtmlEncode(d)}</li>"))}
            </ul>
            <p>If you do not accept these documents before the deadline, your membership access may be temporarily suspended.</p>
            <p><a href="{_settings.BaseUrl}/Consent">Review Documents Now</a></p>
            <p>Thank you,<br/>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var subject = "Welcome to Nobodies Collective!";
        var body = $"""
            <h2>Welcome!</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>Welcome to the Nobodies Collective member portal!</p>
            <p>Here's what you can do:</p>
            <ul>
                <li><a href="{_settings.BaseUrl}/Profile">Complete your profile</a></li>
                <li><a href="{_settings.BaseUrl}/Teams">Join teams and working groups</a></li>
                <li><a href="{_settings.BaseUrl}/Consent">Review legal documents</a></li>
            </ul>
            <p>If you have any questions, feel free to reach out to us.</p>
            <p>Best regards,<br/>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var subject = "Your Membership Access Has Been Suspended";
        var body = $"""
            <h2>Access Suspended</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>Your membership access has been temporarily suspended.</p>
            <p><strong>Reason:</strong> {HtmlEncode(reason)}</p>
            <p>To restore your access, please take the required action:</p>
            <ul>
                <li><a href="{_settings.BaseUrl}/Consent">Review pending consent requirements</a></li>
            </ul>
            <p>If you believe this is an error or have questions, please contact us at
            <a href="mailto:{_settings.AdminAddress}">{_settings.AdminAddress}</a>.</p>
            <p>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        CancellationToken cancellationToken = default)
    {
        var subject = "Verify Your Email Address";
        var body = $"""
            <h2>Email Verification</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>You requested to set <strong>{HtmlEncode(toEmail)}</strong> as your preferred email address.</p>
            <p>Please click the link below to verify this email address:</p>
            <p><a href="{verificationUrl}">Verify Email Address</a></p>
            <p>This link will expire in 24 hours.</p>
            <p>If you did not request this change, you can safely ignore this email.</p>
            <p>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(toEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        CancellationToken cancellationToken = default)
    {
        var formattedDate = deletionDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var subject = "Account Deletion Requested";
        var body = $"""
            <h2>Account Deletion Request Received</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>We have received your request to delete your account. Your account and all associated data
            will be permanently deleted on <strong>{formattedDate}</strong>.</p>
            <p>If you change your mind, you can cancel this request before the deletion date by visiting:</p>
            <p><a href="{_settings.BaseUrl}/Profile/Privacy">Cancel Deletion Request</a></p>
            <p>After deletion, this action cannot be undone and all your data will be permanently removed.</p>
            <p>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var subject = "Your Account Has Been Deleted";
        var body = $"""
            <h2>Account Deleted</h2>
            <p>Dear {HtmlEncode(userName)},</p>
            <p>As requested, your Nobodies Collective account has been permanently deleted.
            All your personal data has been removed from our systems.</p>
            <p>Thank you for being part of our community. If you ever wish to rejoin,
            you're welcome to submit a new membership application.</p>
            <p>Best wishes,<br/>The Nobodies Collective Team</p>
            """;

        await SendEmailAsync(userEmail, subject, body, cancellationToken);
    }

    private async Task SendEmailAsync(
        string toAddress,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = WrapInTemplate(htmlBody),
                TextBody = HtmlToPlainText(htmlBody)
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

    private string WrapInTemplate(string content)
    {
        return """
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <style>
                    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px; }
                    h2 { color: #2c3e50; }
                    a { color: #3498db; }
                    ul { padding-left: 20px; }
                    .footer { margin-top: 30px; padding-top: 20px; border-top: 1px solid #eee; font-size: 12px; color: #666; }
                </style>
            </head>
            <body>
            """ + content + $"""
                <div class="footer">
                    <p>Nobodies Collective<br/>
                    <a href="{_settings.BaseUrl}">{_settings.BaseUrl}</a></p>
                </div>
            </body>
            </html>
            """;
    }

    private static string HtmlToPlainText(string html)
    {
        // Simple HTML to plain text conversion
        var text = html;
        text = System.Text.RegularExpressions.Regex.Replace(text, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(text, "</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(text, "</li>", "\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    private static string HtmlEncode(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text);
    }
}
