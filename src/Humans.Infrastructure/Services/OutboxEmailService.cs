using System.Globalization;
using Hangfire;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Email service implementation that writes messages to the outbox table instead of sending inline.
/// Each method renders the email, wraps it in the template, and inserts an EmailOutboxMessage row.
/// Time-sensitive emails (email_verification) also trigger immediate outbox processing via Hangfire.
/// </summary>
public class OutboxEmailService : IEmailService
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailRenderer _renderer;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ILogger<OutboxEmailService> _logger;
    private readonly EmailSettings _settings;
    private readonly string _environmentName;

    public OutboxEmailService(
        HumansDbContext dbContext,
        IEmailRenderer renderer,
        IHumansMetrics metrics,
        IClock clock,
        IHostEnvironment hostEnvironment,
        IOptions<EmailSettings> settings,
        IBackgroundJobClient backgroundJobClient,
        ILogger<OutboxEmailService> logger)
    {
        _dbContext = dbContext;
        _renderer = renderer;
        _metrics = metrics;
        _clock = clock;
        _backgroundJobClient = backgroundJobClient;
        _logger = logger;
        _settings = settings.Value;
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
        await EnqueueAsync(userEmail, userName, content, "application_approved", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "application_rejected", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "reconsents_required", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "reconsent_reminder", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderWelcome(userName, culture);
        await EnqueueAsync(userEmail, userName, content, "welcome", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "access_suspended", cancellationToken);
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
        await EnqueueAsync(toEmail, userName, content, "email_verification", cancellationToken,
            triggerImmediate: true);
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
        await EnqueueAsync(userEmail, userName, content, "deletion_requested", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderAccountDeleted(userName, culture);
        await EnqueueAsync(userEmail, userName, content, "account_deleted", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "added_to_team", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "signup_rejected", cancellationToken);
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
        await EnqueueAsync(userEmail, userName, content, "term_renewal_reminder", cancellationToken);
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
        await EnqueueAsync(email, name, content, "board_daily_digest", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, culture);
        await EnqueueAsync(userEmail, userName, content, "feedback_response", cancellationToken);
    }

    /// <inheritdoc />
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
        await EnqueueAsync(recipientEmail, recipientName, content, "facilitated_message", cancellationToken,
            replyTo: replyTo);
    }

    private async Task EnqueueAsync(
        string recipientEmail,
        string recipientName,
        EmailContent content,
        string templateName,
        CancellationToken cancellationToken,
        bool triggerImmediate = false,
        string? replyTo = null)
    {
        var wrappedHtml = WrapInTemplate(content.HtmlBody);
        var plainText = HtmlToPlainText(content.HtmlBody);

        // Look up user by email to set UserId for profile email history
        var userId = await _dbContext.UserEmails
            .Where(ue => ue.Email == recipientEmail && ue.IsVerified)
            .Select(ue => (Guid?)ue.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = recipientEmail,
            RecipientName = recipientName,
            Subject = content.Subject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = templateName,
            UserId = userId,
            ReplyTo = replyTo,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = _clock.GetCurrentInstant()
        };

        _dbContext.EmailOutboxMessages.Add(message);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _metrics.RecordEmailQueued(templateName);
        _logger.LogInformation("Email queued: {TemplateName} to {Recipient}", templateName, recipientEmail);

        if (triggerImmediate)
        {
            _backgroundJobClient.Enqueue<ProcessEmailOutboxJob>(x => x.ExecuteAsync(default));
            _logger.LogInformation("Triggered immediate outbox processing for {TemplateName}", templateName);
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

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <style>
                    body { font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 0; background-color: #faf6f0; }
                    h2 { color: #3d2b1f; font-family: 'Cormorant Garamond', Georgia, 'Times New Roman', serif; font-weight: 600; }
                    a { color: #8b6914; }
                    ul { padding-left: 20px; }
                </style>
            </head>
            <body style="font-family: 'Source Sans 3', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; color: #3d2b1f; max-width: 600px; margin: 0 auto; padding: 0; background-color: #faf6f0;">
            {{envBanner}}
            <div style="background: #3d2b1f; padding: 16px 24px; border-bottom: 3px solid #c9a96e;">
                <span style="font-family: Georgia, serif; font-size: 22px; color: #c9a96e; letter-spacing: 0.05em;">Humans</span>
                <span style="font-family: Georgia, serif; font-size: 12px; color: #8b7355; margin-left: 8px; letter-spacing: 0.1em;">NOBODIES COLLECTIVE</span>
            </div>
            <div style="padding: 28px 24px 20px 24px;">
            {{content}}
            </div>
            <div style="background: #f0e2c8; padding: 16px 24px; border-top: 1px solid #e8d4ab;">
                <p style="font-size: 12px; color: #6b5a4e; margin: 0; line-height: 1.5;">
                    Humans &mdash; Nobodies Collective<br>
                    <a href="{{_settings.BaseUrl}}" style="color: #8b6914;">{{_settings.BaseUrl}}</a>
                </p>
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
