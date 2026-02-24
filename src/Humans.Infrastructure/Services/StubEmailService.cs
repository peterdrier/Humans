using Microsoft.Extensions.Logging;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;

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

    public Task SendBoardDailyDigestAsync(
        string email,
        string name,
        string date,
        IReadOnlyList<BoardDigestTierGroup> groups,
        BoardDigestOutstandingCounts? outstandingCounts = null,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[STUB] Would send Board daily digest to {Email} ({Name}) [Culture: {Culture}] for {Date} with {GroupCount} tier groups, outstanding: {@Outstanding}",
            email, name, culture, date, groups.Count, outstandingCounts);
        return Task.CompletedTask;
    }
}
