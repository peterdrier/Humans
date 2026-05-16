using Microsoft.Extensions.Logging;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Email;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Stub implementation of IEmailService that logs actions without sending real emails.
/// Used for local/dev scenarios where SMTP transport is intentionally disabled.
/// </summary>
public class StubEmailService : IEmailService
{
    private readonly ILogger<StubEmailService> _logger;

    public StubEmailService(ILogger<StubEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send application approved email to {Email} ({UserName}) [Culture: {Culture}]",
            userEmail, userName, culture);
        return Task.CompletedTask;
    }

    public Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        MembershipTier tier,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send application rejected email to {Email} ({UserName}) [Culture: {Culture}]. Reason: {Reason}",
            userEmail, userName, culture, reason);
        return Task.CompletedTask;
    }

    public Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send re-consent required email to {Email} ({UserName}) [Culture: {Culture}] for document: {DocumentName}",
            userEmail, userName, culture, documentName);
        return Task.CompletedTask;
    }

    public Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send re-consent required email to {Email} ({UserName}) [Culture: {Culture}] for documents: {DocumentNames}",
            userEmail, userName, culture, string.Join(", ", documentNames));
        return Task.CompletedTask;
    }

    public Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send re-consent reminder to {Email} ({UserName}) [Culture: {Culture}]. Documents: {Documents}. Days remaining: {Days}",
            userEmail, userName, culture, string.Join(", ", documentNames), daysRemaining);
        return Task.CompletedTask;
    }

    public Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send welcome email to {Email} ({UserName}) [Culture: {Culture}]",
            userEmail, userName, culture);
        return Task.CompletedTask;
    }

    public Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send access suspended email to {Email} ({UserName}) [Culture: {Culture}]. Reason: {Reason}",
            userEmail, userName, culture, reason);
        return Task.CompletedTask;
    }

    public Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        bool isConflict = false,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send email verification to {Email} ({UserName}) [Culture: {Culture}]. Verification URL: {Url}",
            toEmail, userName, culture, verificationUrl);
        return Task.CompletedTask;
    }

    public Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send account deletion requested email to {Email} ({UserName}) [Culture: {Culture}]. Deletion date: {Date}",
            userEmail, userName, culture, deletionDate);
        return Task.CompletedTask;
    }

    public Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send account deleted confirmation to {Email} ({UserName}) [Culture: {Culture}]",
            userEmail, userName, culture);
        return Task.CompletedTask;
    }

    public Task SendAddedToTeamAsync(
        string userEmail,
        string userName,
        string teamName,
        string teamSlug,
        IEnumerable<(string Name, string? Url)> resources,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send added-to-team email to {Email} ({UserName}) [Culture: {Culture}] for team {TeamName}",
            userEmail, userName, culture, teamName);
        return Task.CompletedTask;
    }

    public Task SendSignupRejectedAsync(
        string userEmail,
        string userName,
        string? reason,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send signup rejected email to {Email} ({UserName}) [Culture: {Culture}]. Reason: {Reason}",
            userEmail, userName, culture, reason);
        return Task.CompletedTask;
    }

    public Task SendTermRenewalReminderAsync(
        string userEmail,
        string userName,
        string tierName,
        string expiresAt,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send term renewal reminder to {Email} ({UserName}) [Culture: {Culture}] for {Tier} expiring {ExpiresAt}",
            userEmail, userName, culture, tierName, expiresAt);
        return Task.CompletedTask;
    }

    public Task SendFeedbackResponseAsync(
        string userEmail, string userName, string originalDescription,
        string responseMessage, string reportLink, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send feedback response to {Email} ({UserName}) [Culture: {Culture}] Link: {ReportLink}",
            userEmail, userName, culture, reportLink);
        return Task.CompletedTask;
    }

    public Task SendFacilitatedMessageAsync(
        string recipientEmail,
        string recipientName,
        string senderName,
        string messageText,
        bool includeContactInfo,
        string? senderEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send facilitated message to {Email} ({RecipientName}) from {SenderName} [Culture: {Culture}] [IncludeContactInfo: {IncludeContact}]",
            recipientEmail, recipientName, senderName, culture, includeContactInfo);
        return Task.CompletedTask;
    }

    public Task SendCoordinatorRotaMessageAsync(
        CoordinatorRotaMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogInformation(
            "[STUB] Would send coordinator rota message to {Email} ({RecipientName}) from {SenderName} for rota {RotaName} ({ShiftCount} shifts) [Culture: {Culture}]",
            request.RecipientEmail, request.RecipientName, request.SenderName, request.RotaName,
            request.ShiftLines.Count, request.Culture);
        return Task.CompletedTask;
    }

    public Task SendMagicLinkLoginAsync(
        string toEmail, string displayName, string magicLinkUrl,
        string? culture = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would send magic link login to {Email} ({Name})", toEmail, displayName);
        return Task.CompletedTask;
    }

    public Task SendMagicLinkSignupAsync(
        string toEmail, string magicLinkUrl,
        string? culture = null, CancellationToken ct = default)
    {
        _logger.LogInformation("[STUB] Would send magic link signup to {Email}", toEmail);
        return Task.CompletedTask;
    }

    public Task SendWorkspaceCredentialsAsync(
        string recoveryEmail, string userName, string workspaceEmail, string tempPassword,
        string? culture = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Would send workspace credentials for {WorkspaceEmail} to {RecoveryEmail}", workspaceEmail, recoveryEmail);
        return Task.CompletedTask;
    }

    public Task SendCampaignCodeAsync(CampaignCodeEmailRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send campaign code email to {Email} ({Name}) for grant {GrantId}",
            request.RecipientEmail, request.RecipientName, request.CampaignGrantId);
        return Task.CompletedTask;
    }

    public Task SendEventLifecycleNotificationAsync(
        EventLifecycleNotification request,
        string userEmail,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send event {Status} email to {Email} ({UserName}) for '{Title}'. Reason: {Reason}",
            request.NewStatus, userEmail, request.UserName, request.EventTitle, request.Reason);
        return Task.CompletedTask;
    }

    public Task SendGoogleGroupRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string groupName,
        string groupEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send Google group removal (loss-of-access) to {Email} ({UserName}) [Culture: {Culture}] group {GroupName} ({GroupEmail})",
            removedEmail, userName, culture, groupName, groupEmail);
        return Task.CompletedTask;
    }

    public Task SendGoogleDriveRemovalLossOfAccessAsync(
        string removedEmail,
        string userName,
        string folderName,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send Google Drive removal (loss-of-access) to {Email} ({UserName}) [Culture: {Culture}] folder {FolderName}",
            removedEmail, userName, culture, folderName);
        return Task.CompletedTask;
    }

    public Task SendGoogleAccessRemovalSecondaryCleanupAsync(
        string removedEmail,
        string userName,
        string currentGoogleEmail,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send Google access removal (secondary cleanup) to {Email} ({UserName}) [Culture: {Culture}] primary now {Primary}",
            removedEmail, userName, culture, currentGoogleEmail);
        return Task.CompletedTask;
    }

    public Task SendIssueCommentAsync(
        string to,
        string displayName,
        string issueTitle,
        string commentContent,
        string issueLink,
        string preferredLanguage,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[STUB] Would send issue comment email to {Email} ({Name}) [Lang: {Lang}] for issue {Title} link {Link}",
            to, displayName, preferredLanguage, issueTitle, issueLink);
        return Task.CompletedTask;
    }
}
