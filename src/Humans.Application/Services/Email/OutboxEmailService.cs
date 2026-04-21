using System.Globalization;
using System.Text.Json;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Email;

/// <summary>
/// Application-layer implementation of <see cref="IEmailService"/>: renders
/// each system email through <see cref="IEmailRenderer"/>, wraps the body
/// with <see cref="IEmailBodyComposer"/>, and appends a row to the outbox
/// through <see cref="IEmailOutboxRepository"/>. Time-sensitive templates
/// (email verification, magic-link, workspace credentials) also trigger an
/// immediate processor run through <see cref="IImmediateOutboxProcessor"/>.
/// </summary>
/// <remarks>
/// The SMTP-send path lives in <c>ProcessEmailOutboxJob</c> (Infrastructure)
/// and dispatches through <see cref="IEmailTransport"/>. This service is
/// intentionally cache- and transport-free.
/// </remarks>
public sealed class OutboxEmailService : IEmailService
{
    private readonly IEmailOutboxRepository _outboxRepo;
    private readonly IUserEmailService _userEmailService;
    private readonly IEmailRenderer _renderer;
    private readonly IEmailBodyComposer _bodyComposer;
    private readonly IImmediateOutboxProcessor _immediateProcessor;
    private readonly IHumansMetrics _metrics;
    private readonly IClock _clock;
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly ILogger<OutboxEmailService> _logger;

    public OutboxEmailService(
        IEmailOutboxRepository outboxRepo,
        IUserEmailService userEmailService,
        IEmailRenderer renderer,
        IEmailBodyComposer bodyComposer,
        IImmediateOutboxProcessor immediateProcessor,
        IHumansMetrics metrics,
        IClock clock,
        ICommunicationPreferenceService commPrefService,
        ILogger<OutboxEmailService> logger)
    {
        _outboxRepo = outboxRepo;
        _userEmailService = userEmailService;
        _renderer = renderer;
        _bodyComposer = bodyComposer;
        _immediateProcessor = immediateProcessor;
        _metrics = metrics;
        _clock = clock;
        _commPrefService = commPrefService;
        _logger = logger;
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
        // Invariant long-date formatter, matches Humans.Infrastructure.Helpers.EmailDateTimeExtensions
        // (kept here to avoid an Infrastructure→Application back-reference).
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
        await EnqueueAsync(userEmail, userName, content, "signup_rejected", cancellationToken,
            category: MessageCategory.System);
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
        await EnqueueAsync(userEmail, userName, content, "term_renewal_reminder", cancellationToken,
            category: MessageCategory.Governance);
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
        await EnqueueAsync(email, name, content, "board_daily_digest", cancellationToken,
            category: MessageCategory.Governance);
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
        await EnqueueAsync(userEmail, userName, content, "feedback_response", cancellationToken,
            category: MessageCategory.System);
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

    /// <inheritdoc />
    public async Task SendCampaignCodeAsync(CampaignCodeEmailRequest request, CancellationToken cancellationToken = default)
    {
        // Renderer performs the {{Code}} / {{Name}} substitution and markdown→HTML
        // conversion (with HTML-encoded substitutions to prevent injection).
        var rendered = _renderer.RenderCampaignCode(
            request.Subject, request.MarkdownBody, request.Code, request.RecipientName);

        // Generate unsubscribe headers and footer link for the CampaignCodes category
        var unsubHeaders = _commPrefService.GenerateUnsubscribeHeaders(request.UserId, MessageCategory.CampaignCodes);
        string? extraHeadersJson = null;
        if (unsubHeaders is not null)
        {
            extraHeadersJson = JsonSerializer.Serialize(unsubHeaders);
        }
        var unsubscribeUrl = _commPrefService.GenerateBrowserUnsubscribeUrl(request.UserId, MessageCategory.CampaignCodes);

        var (wrappedHtml, plainText) = _bodyComposer.Compose(rendered.HtmlBody, unsubscribeUrl);

        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            RecipientEmail = request.RecipientEmail,
            RecipientName = request.RecipientName,
            Subject = rendered.Subject,
            HtmlBody = wrappedHtml,
            PlainTextBody = plainText,
            TemplateName = "campaign_code",
            UserId = request.UserId,
            CampaignGrantId = request.CampaignGrantId,
            ReplyTo = request.ReplyTo,
            ExtraHeaders = extraHeadersJson,
            Status = EmailOutboxStatus.Queued,
            CreatedAt = _clock.GetCurrentInstant()
        };

        await _outboxRepo.AddAsync(message, cancellationToken);

        _metrics.RecordEmailQueued("campaign_code");
        _logger.LogInformation(
            "Campaign code email queued for grant {GrantId} to {Recipient}",
            request.CampaignGrantId, request.RecipientEmail);
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
        // Look up user by email to set UserId for profile email history.
        // Routed through IUserEmailService so the Profile section's user_emails
        // table stays behind its owning service.
        var userId = await _userEmailService.GetUserIdByVerifiedEmailAsync(recipientEmail, cancellationToken);

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
            extraHeadersJson = JsonSerializer.Serialize(headers);
            unsubscribeUrl = _commPrefService.GenerateBrowserUnsubscribeUrl(userId.Value, category.Value);
        }

        var (wrappedHtml, plainText) = _bodyComposer.Compose(content.HtmlBody, unsubscribeUrl);

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

        await _outboxRepo.AddAsync(message, cancellationToken);

        _metrics.RecordEmailQueued(templateName);
        _logger.LogInformation("Email queued: {TemplateName} to {Recipient}", templateName, recipientEmail);

        if (triggerImmediate)
        {
            _immediateProcessor.TriggerImmediate();
            _logger.LogInformation("Triggered immediate outbox processing for {TemplateName}", templateName);
        }
    }
}
