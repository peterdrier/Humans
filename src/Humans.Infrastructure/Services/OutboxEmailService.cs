using Hangfire;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Helpers;
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
    private readonly ICommunicationPreferenceService _commPrefService;
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
        ICommunicationPreferenceService commPrefService,
        ILogger<OutboxEmailService> logger)
    {
        _dbContext = dbContext;
        _renderer = renderer;
        _metrics = metrics;
        _clock = clock;
        _backgroundJobClient = backgroundJobClient;
        _commPrefService = commPrefService;
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
        bool isConflict = false,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderEmailVerification(userName, toEmail, verificationUrl, isConflict, culture);
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
        var formattedDate = deletionDate.ToInvariantLongDate();
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
        await EnqueueAsync(userEmail, userName, content, "added_to_team", cancellationToken,
            category: MessageCategory.TeamUpdates);
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
    public async Task SendAdminDailyDigestAsync(
        string email,
        string name,
        string date,
        AdminDigestCounts counts,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderAdminDailyDigest(name, date, counts, culture);
        await EnqueueAsync(email, name, content, "admin_daily_digest", cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string reportLink, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, reportLink, culture);
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
            replyTo: replyTo, category: MessageCategory.FacilitatedMessages);
    }

    /// <inheritdoc />
    public async Task SendMagicLinkLoginAsync(
        string toEmail,
        string displayName,
        string magicLinkUrl,
        string? culture = null,
        CancellationToken ct = default)
    {
        var content = _renderer.RenderMagicLinkLogin(displayName, magicLinkUrl, culture);
        await EnqueueAsync(toEmail, displayName, content, "magic_link_login", ct,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendMagicLinkSignupAsync(
        string toEmail,
        string magicLinkUrl,
        string? culture = null,
        CancellationToken ct = default)
    {
        var content = _renderer.RenderMagicLinkSignup(magicLinkUrl, culture);
        await EnqueueAsync(toEmail, toEmail, content, "magic_link_signup", ct,
            triggerImmediate: true);
    }

    /// <inheritdoc />
    public async Task SendWorkspaceCredentialsAsync(
        string recoveryEmail,
        string userName,
        string workspaceEmail,
        string tempPassword,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var content = _renderer.RenderWorkspaceCredentials(userName, workspaceEmail, tempPassword, culture);
        await EnqueueAsync(recoveryEmail, userName, content, "workspace_credentials", cancellationToken,
            triggerImmediate: true);
    }

    private async Task EnqueueAsync(
        string recipientEmail,
        string recipientName,
        EmailContent content,
        string templateName,
        CancellationToken cancellationToken,
        bool triggerImmediate = false,
        string? replyTo = null,
        MessageCategory? category = null)
    {
        // Look up user by email to set UserId for profile email history
        var userId = await _dbContext.UserEmails
            .Where(ue => ue.Email == recipientEmail && ue.IsVerified)
            .Select(ue => (Guid?)ue.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        // Check opt-out for non-System categories
        if (category is not null && category != MessageCategory.System && userId.HasValue)
        {
            if (await _commPrefService.IsOptedOutAsync(userId.Value, category.Value, cancellationToken))
            {
                _logger.LogInformation(
                    "Email suppressed: {TemplateName} to {Recipient} — opted out of {Category}",
                    templateName, recipientEmail, category.Value);
                return;
            }
        }

        // Generate unsubscribe URL and headers for opt-outable categories
        string? unsubscribeUrl = null;
        string? extraHeadersJson = null;
        if (category is not null && category != MessageCategory.System && userId.HasValue)
        {
            var headers = _commPrefService.GenerateUnsubscribeHeaders(userId.Value, category.Value);
            extraHeadersJson = System.Text.Json.JsonSerializer.Serialize(headers);

            // Extract URL for footer link (strip angle brackets from List-Unsubscribe header)
            if (headers.TryGetValue("List-Unsubscribe", out var listUnsub))
                unsubscribeUrl = listUnsub.Trim('<', '>');
        }

        var (wrappedHtml, plainText) = EmailBodyComposer.Compose(
            content.HtmlBody, _settings.BaseUrl, _environmentName, unsubscribeUrl);

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
            ExtraHeaders = extraHeadersJson,
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
}
