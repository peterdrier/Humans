using System.Globalization;
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

namespace Humans.Infrastructure.Services;

/// <summary>
/// Email service implementation using SMTP via MailKit.
/// Delegates rendering to IEmailRenderer; handles transport only.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<SmtpEmailService> _logger;
    private readonly IEmailRenderer _renderer;
    private readonly string _environmentName;

    public SmtpEmailService(
        IOptions<EmailSettings> settings,
        HumansMetricsService metrics,
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
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderEmailVerification(userName, toEmail, verificationUrl, culture);
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
        var formattedDate = deletionDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
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
        var isProduction = string.Equals(_environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        var envLabel = string.Equals(_environmentName, "Staging", StringComparison.OrdinalIgnoreCase)
            ? "QA"
            : _environmentName.ToUpperInvariant();
        var envBanner = isProduction
            ? ""
            : $"""
                <div style="background:#a0522d;color:#fff;text-align:center;font-size:11px;font-weight:700;letter-spacing:0.15em;text-transform:uppercase;padding:4px 0;">
                    {System.Net.WebUtility.HtmlEncode(envLabel)} &bull; {System.Net.WebUtility.HtmlEncode(envLabel)} &bull; {System.Net.WebUtility.HtmlEncode(envLabel)}
                </div>
                """;

        return """
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <style>
                    body { font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #faf6f0; }
                    h2 { color: #3d2b1f; font-family: 'Cormorant Garamond', Georgia, 'Times New Roman', serif; font-weight: 600; }
                    a { color: #8b6914; }
                    ul { padding-left: 20px; }
                    .footer { margin-top: 30px; padding-top: 20px; border-top: 1px solid #c9a96e; font-size: 12px; color: #6b5a4e; }
                </style>
            </head>
            <body>
            """ + envBanner + content + $"""
                <div class="footer">
                    <p>Humans &mdash; Nobodies Collective<br/>
                    <a href="{_settings.BaseUrl}">{_settings.BaseUrl}</a></p>
                </div>
            </body>
            </html>
            """;
    }

    private static string HtmlToPlainText(string html)
    {
        var text = html;
        text = System.Text.RegularExpressions.Regex.Replace(text, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(text, "</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(text, "</li>", "\n", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Trim();
    }
}
