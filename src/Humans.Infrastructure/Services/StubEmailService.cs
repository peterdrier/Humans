using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Stub implementation of IEmailService that logs actions without sending real emails.
/// Replace with real implementation when email service integration is ready.
/// </summary>
public class StubEmailService : IEmailService
{
    private readonly ILogger<StubEmailService> _logger;

    public StubEmailService(ILogger<StubEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendApplicationSubmittedAsync(
        Guid applicationId,
        string applicantName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send application submitted email for application {ApplicationId} by {ApplicantName}",
            applicationId, applicantName);
        return Task.CompletedTask;
    }

    public Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send application approved email to {Email} ({UserName})",
            userEmail, userName);
        return Task.CompletedTask;
    }

    public Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send application rejected email to {Email} ({UserName}). Reason: {Reason}",
            userEmail, userName, reason);
        return Task.CompletedTask;
    }

    public Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send re-consent required email to {Email} ({UserName}) for document: {DocumentName}",
            userEmail, userName, documentName);
        return Task.CompletedTask;
    }

    public Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send re-consent required email to {Email} ({UserName}) for documents: {DocumentNames}",
            userEmail, userName, string.Join(", ", documentNames));
        return Task.CompletedTask;
    }

    public Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send re-consent reminder to {Email} ({UserName}). Documents: {Documents}. Days remaining: {Days}",
            userEmail, userName, string.Join(", ", documentNames), daysRemaining);
        return Task.CompletedTask;
    }

    public Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send welcome email to {Email} ({UserName})",
            userEmail, userName);
        return Task.CompletedTask;
    }

    public Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send access suspended email to {Email} ({UserName}). Reason: {Reason}",
            userEmail, userName, reason);
        return Task.CompletedTask;
    }

    public Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send email verification to {Email} ({UserName}). Verification URL: {Url}",
            toEmail, userName, verificationUrl);
        return Task.CompletedTask;
    }

    public Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send account deletion requested email to {Email} ({UserName}). Deletion date: {Date}",
            userEmail, userName, deletionDate);
        return Task.CompletedTask;
    }

    public Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send account deleted confirmation to {Email} ({UserName})",
            userEmail, userName);
        return Task.CompletedTask;
    }

    public Task SendAddedToTeamAsync(
        string userEmail,
        string userName,
        string teamName,
        string teamSlug,
        IEnumerable<(string Name, string? Url)> resources,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send added-to-team email to {Email} ({UserName}) for team {TeamName}",
            userEmail, userName, teamName);
        return Task.CompletedTask;
    }

    public Task SendSignupRejectedAsync(
        string userEmail,
        string userName,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send signup rejected email to {Email} ({UserName}). Reason: {Reason}",
            userEmail, userName, reason);
        return Task.CompletedTask;
    }

    public Task SendTermRenewalReminderAsync(
        string userEmail,
        string userName,
        string tierName,
        string expiresAt,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send term renewal reminder to {Email} ({UserName}) for {Tier} expiring {ExpiresAt}",
            userEmail, userName, tierName, expiresAt);
        return Task.CompletedTask;
    }
}
