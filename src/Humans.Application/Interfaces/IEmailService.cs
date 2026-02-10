namespace Humans.Application.Interfaces;

/// <summary>
/// Service for sending email notifications.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends an application submitted notification to administrators.
    /// </summary>
    /// <param name="applicationId">The application ID.</param>
    /// <param name="applicantName">The applicant's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendApplicationSubmittedAsync(
        Guid applicationId,
        string applicantName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an application approved notification to the applicant.
    /// </summary>
    /// <param name="userEmail">The applicant's email.</param>
    /// <param name="userName">The applicant's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendApplicationApprovedAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an application rejected notification to the applicant.
    /// </summary>
    /// <param name="userEmail">The applicant's email.</param>
    /// <param name="userName">The applicant's name.</param>
    /// <param name="reason">The reason for rejection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendApplicationRejectedAsync(
        string userEmail,
        string userName,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a legal document updated notification requiring re-consent.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="documentName">The document name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReConsentRequiredAsync(
        string userEmail,
        string userName,
        string documentName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a notification for multiple legal document updates requiring re-consent.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="documentNames">The names of updated documents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReConsentsRequiredAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a re-consent reminder before access is suspended.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="documentNames">Names of documents requiring consent.</param>
    /// <param name="daysRemaining">Days remaining before suspension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReConsentReminderAsync(
        string userEmail,
        string userName,
        IEnumerable<string> documentNames,
        int daysRemaining,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a welcome email to a new member.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendWelcomeEmailAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an access suspended notification.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="reason">The reason for suspension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAccessSuspendedAsync(
        string userEmail,
        string userName,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an email verification link for the preferred email address.
    /// </summary>
    /// <param name="toEmail">The email address to verify.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="verificationUrl">The URL to verify the email.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendEmailVerificationAsync(
        string toEmail,
        string userName,
        string verificationUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends confirmation that account deletion has been requested.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="deletionDate">When the account will be deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAccountDeletionRequestedAsync(
        string userEmail,
        string userName,
        DateTime deletionDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends confirmation that account has been deleted.
    /// </summary>
    /// <param name="userEmail">The user's email.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAccountDeletedAsync(
        string userEmail,
        string userName,
        CancellationToken cancellationToken = default);
}
