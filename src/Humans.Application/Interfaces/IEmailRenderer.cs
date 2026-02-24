using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Rendered email content (subject + HTML body).
/// </summary>
public record EmailContent(string Subject, string HtmlBody);

/// <summary>
/// Renders email subject and body HTML for all system email types.
/// Separated from IEmailService (SMTP transport) to enable reuse by admin previews
/// and to centralize localization of email body text.
/// </summary>
public interface IEmailRenderer
{
    /// <summary>
    /// Application submitted notification (to admin, always English).
    /// </summary>
    EmailContent RenderApplicationSubmitted(Guid applicationId, string applicantName);

    /// <summary>
    /// Application approved notification.
    /// </summary>
    EmailContent RenderApplicationApproved(string userName, MembershipTier tier, string? culture = null);

    /// <summary>
    /// Application rejected notification.
    /// </summary>
    EmailContent RenderApplicationRejected(string userName, MembershipTier tier, string reason, string? culture = null);

    /// <summary>
    /// Signup rejected notification.
    /// </summary>
    EmailContent RenderSignupRejected(string userName, string? reason, string? culture = null);

    /// <summary>
    /// Re-consent required notification (one or more documents).
    /// </summary>
    EmailContent RenderReConsentsRequired(string userName, IReadOnlyList<string> documentNames, string? culture = null);

    /// <summary>
    /// Re-consent reminder before suspension.
    /// </summary>
    EmailContent RenderReConsentReminder(string userName, IReadOnlyList<string> documentNames, int daysRemaining, string? culture = null);

    /// <summary>
    /// Welcome email for new humans.
    /// </summary>
    EmailContent RenderWelcome(string userName, string? culture = null);

    /// <summary>
    /// Access suspended notification.
    /// </summary>
    EmailContent RenderAccessSuspended(string userName, string reason, string? culture = null);

    /// <summary>
    /// Email verification link.
    /// </summary>
    EmailContent RenderEmailVerification(string userName, string toEmail, string verificationUrl, string? culture = null);

    /// <summary>
    /// Account deletion requested confirmation.
    /// </summary>
    EmailContent RenderAccountDeletionRequested(string userName, string formattedDeletionDate, string? culture = null);

    /// <summary>
    /// Account deleted confirmation.
    /// </summary>
    EmailContent RenderAccountDeleted(string userName, string? culture = null);

    /// <summary>
    /// Added to team notification.
    /// </summary>
    EmailContent RenderAddedToTeam(string userName, string teamName, string teamSlug, IReadOnlyList<(string Name, string? Url)> resources, string? culture = null);

    /// <summary>
    /// Term renewal reminder for Colaborador/Asociado.
    /// </summary>
    EmailContent RenderTermRenewalReminder(string userName, string tierName, string expiresAt, string? culture = null);

    /// <summary>
    /// Board daily digest of new approvals and outstanding items.
    /// </summary>
    EmailContent RenderBoardDailyDigest(string boardMemberName, string date, IReadOnlyList<BoardDigestTierGroup> tierGroups, BoardDigestOutstandingCounts? outstandingCounts = null, string? culture = null);
}
